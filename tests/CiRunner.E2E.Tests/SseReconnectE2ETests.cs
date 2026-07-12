using CiRunner.E2E.Tests.Support;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace CiRunner.E2E.Tests;

/// <summary>L4 test for SSE auto-reconnect (ci-runner-test-spec.md §4 E2E-016; spec §5 F5
/// "ポーリング不要の状態更新も SSE(/api/events)で通知"). connectGlobalEvents() (index.html) opens a
/// single EventSource('/api/events') for the page's lifetime and re-renders the current view (jobs
/// list / job history / build detail / queue) on every message. The sidebar's separate 5s
/// refreshStatus() poll only updates the executor/queue counters - it never calls renderJobsList() -
/// so a jobs-list row updating to show a brand-new build can only be explained by the SSE channel
/// itself having delivered a fresh message, which after a simulated network blip is only possible if
/// the browser's built-in EventSource reconnect logic kicked back in on its own.</summary>
public class SseReconnectE2ETests : IAsyncLifetime
{
    private static readonly TestLocalUser Admin = new("admin", "admin123", "admin");
    private PlaywrightFixture _pw = null!;

    public async Task InitializeAsync()
    {
        _pw = new PlaywrightFixture();
        await _pw.InitializeAsync();
    }

    public async Task DisposeAsync() => await _pw.DisposeAsync();

    private static async Task<IPage> LoggedInPageAsync(HostProcess host, IBrowser browser)
    {
        var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync(host.BaseUrl);
        await page.ClickAsync("#signin-btn");
        await page.FillAsync("#login-username", "admin");
        await page.FillAsync("#login-password", "admin123");
        await page.ClickAsync("#login-submit");
        await Expect(page.Locator("#user-name")).ToContainTextAsync("admin");
        // Let boot()'s initial async renderRoute('/jobs') finish before the caller navigates
        // anywhere: its fetches resolve on their own schedule and the LAST writer wins
        // main.innerHTML, so navigating mid-flight would let a stale jobs-list render overwrite
        // the caller's freshly rendered view.
        await Expect(page.Locator(".statgrid")).ToBeVisibleAsync(new() { Timeout = 10000 });
        return page;
    }

    private static async Task CreateJobAsync(HostProcess host, string name, string pipeline)
    {
        await HttpJson.PostAsync(host.Client, "/api/login", new { username = "admin", password = "admin123" });
        var createRes = await HttpJson.PostAsync(host.Client, "/api/admin/jobs", new { name });
        createRes.EnsureSuccessStatusCode();
        File.WriteAllText(Path.Combine(host.Root, "jobs", name, "pipeline.cipipe"), pipeline);
    }

    private static async Task TriggerAsync(HostProcess host, string name)
    {
        var res = await host.Client.PostAsync($"/api/jobs/{name}/trigger", null);
        res.EnsureSuccessStatusCode();
    }

    // E2E-016: simulate a network blip while sitting on the jobs list, then trigger a build through
    // a completely separate HTTP client (bypassing the browser's own, currently-offline network
    // stack), and confirm the jobs list picks up the new build with no page reload anywhere in the
    // test - the only path that can explain the DOM updating in place is connectGlobalEvents()'s
    // EventSource having reconnected by itself and received a later build-lifecycle broadcast.
    [Fact]
    public async Task JobsList_ReflectsNewBuildAfterNetworkBlip_WithoutPageReload_E2E016()
    {
        await using var host = await HostProcess.StartAsync(new[] { Admin }, initialAdmins: new[] { "admin" });
        // A few seconds of Running time spreads this build's stage-start/stage-end/build-finished
        // SSE broadcasts (BuildDispatcher/BuildRunner, via GlobalEventHub) out over a window
        // comfortably longer than a browser's default EventSource reconnect delay (commonly ~3s) -
        // GlobalEventHub does not replay missed messages to a not-yet-reconnected subscriber, so the
        // very first "build-started" broadcast (fired the instant the build is triggered, while the
        // client is still offline/reconnecting) is expected to be missed; a later broadcast for the
        // same build is what this test actually relies on landing after reconnect.
        await CreateJobAsync(host, "sse-job", """
            Stage "Work" {
                Start-Sleep -Seconds 4
                Write-Host "done"
            }
            """);

        var page = await LoggedInPageAsync(host, _pw.Browser);
        await page.Locator("[data-nav=jobs]").ClickAsync();
        var jobRow = page.Locator("[data-testid='job-row-sse-job']");
        await Expect(jobRow).ToBeVisibleAsync(new() { Timeout = 10000 });
        await Expect(jobRow).ToContainTextAsync("no builds");

        // A page-scoped marker: an actual page reload/navigation would reset this global, unlike an
        // in-place script-driven DOM re-render - the strongest signal available (short of
        // instrumenting product code) that whatever updates the row below did so without reloading.
        await page.EvaluateAsync("() => { window.__e2e016NoReload = true; }");

        await page.Context.SetOfflineAsync(true);
        await Task.Delay(1500);
        await page.Context.SetOfflineAsync(false);

        // Triggered on a plain HttpClient entirely separate from the browser's network stack, so this
        // succeeds regardless of the browser context's offline state at the moment of the call.
        await TriggerAsync(host, "sse-job");

        await Expect(jobRow).ToContainTextAsync("#1", new() { Timeout = 20000 });

        var noReload = await page.EvaluateAsync<bool>("() => window.__e2e016NoReload === true");
        Assert.True(noReload, "page must not have reloaded/navigated - the update must come from the reconnected EventSource re-rendering in place");
    }
}
