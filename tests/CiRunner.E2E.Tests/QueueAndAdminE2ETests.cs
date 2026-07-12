using CiRunner.E2E.Tests.Support;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace CiRunner.E2E.Tests;

/// <summary>L4 tests for the queue screen and admin Settings/Resources tabs (ci-runner-test-spec.md
/// §4 E2E-010/011/014). Jobs are created/triggered via the admin HTTP API (fast, deterministic) since
/// these scenarios verify queue/admin UI behavior, not the job-creation form itself (covered by
/// E2E-002).</summary>
public class QueueAndAdminE2ETests : IAsyncLifetime
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
        return page;
    }

    private static async Task CreateJobAsync(HostProcess host, string name, string pipeline, string[]? resources = null)
    {
        await HttpJson.PostAsync(host.Client, "/api/login", new { username = "admin", password = "admin123" });
        var createRes = await HttpJson.PostAsync(host.Client, "/api/admin/jobs", new { name, resources = resources ?? Array.Empty<string>() });
        createRes.EnsureSuccessStatusCode();
        File.WriteAllText(Path.Combine(host.Root, "jobs", name, "pipeline.cipipe"), pipeline);
    }

    private static async Task<long> TriggerAsync(HostProcess host, string name)
    {
        var res = await host.Client.PostAsync($"/api/jobs/{name}/trigger", null);
        res.EnsureSuccessStatusCode();
        using var body = await HttpJson.ReadJsonAsync(res);
        return body.RootElement.GetProperty("id").GetInt64();
    }

    private static async Task<string?> GetStatusAsync(HostProcess host, long buildId)
    {
        using var doc = await HttpJson.ReadJsonAsync(await host.Client.GetAsync($"/api/builds/{buildId}"));
        return doc.RootElement.GetProperty("status").GetString();
    }

    private static async Task WaitForStatusAsync(HostProcess host, long buildId, string status, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        string? last = null;
        while (DateTime.UtcNow < deadline)
        {
            last = await GetStatusAsync(host, buildId);
            if (last == status)
            {
                return;
            }
            await Task.Delay(100);
        }
        throw new TimeoutException($"build {buildId} did not reach status '{status}' in time (last seen: '{last}')");
    }

    // E2E-010: a resource-blocked build shows "which build is blocking it" on the queue screen, and can
    // be cancelled (Abort, from its own build detail page) rather than waiting it out.
    [Fact]
    public async Task QueueScreen_ShowsBlockingBuildAndAllowsCancellation_E2E010()
    {
        await using var host = await HostProcess.StartAsync(new[] { Admin }, initialAdmins: new[] { "admin" });
        await CreateJobAsync(host, "queue-holder", """Stage "Work" { Start-Sleep -Seconds 30 }""", new[] { "bench-1" });
        await CreateJobAsync(host, "queue-waiter", """Stage "Work" { Write-Host "done" }""", new[] { "bench-1" });

        var holderId = await TriggerAsync(host, "queue-holder");
        await WaitForStatusAsync(host, holderId, "running", TimeSpan.FromSeconds(10));
        var waiterId = await TriggerAsync(host, "queue-waiter");
        await WaitForStatusAsync(host, waiterId, "waiting", TimeSpan.FromSeconds(10));

        var page = await LoggedInPageAsync(host, _pw.Browser);
        await page.Locator("[data-nav=queue]").ClickAsync();

        await Expect(page.GetByText("queue-waiter")).ToBeVisibleAsync();
        var blockedByText = page.Locator(".sub", new() { HasText = "blocked by" });
        await Expect(blockedByText).ToContainTextAsync("bench-1");
        await Expect(blockedByText).ToContainTextAsync("queue-holder");

        // Cancel it: the queue screen links out to the blocking build, but the waiting build's own
        // Abort (build detail page) is how a queued/waiting build is withdrawn (spec §5 F3 - the same
        // mechanism used for a running build's Abort).
        await page.GotoAsync(host.BaseUrl + $"#/builds/{waiterId}");
        var abortBtn = page.Locator("[data-testid=abort-btn]");
        await Expect(abortBtn).ToBeVisibleAsync(new() { Timeout = 10000 });
        page.Dialog += (_, dialog) => dialog.AcceptAsync();
        await abortBtn.ClickAsync();
        await Expect(page.Locator(".badge-status", new() { HasTextString = "Aborted" })).ToBeVisibleAsync(new() { Timeout = 10000 });

        await page.Locator("[data-nav=queue]").ClickAsync();
        await Expect(page.GetByText("Queue is empty.")).ToBeVisibleAsync(new() { Timeout = 10000 });
    }

    // E2E-011: raising Executors in system settings (no restart) lets a second build start running
    // concurrently instead of waiting for the first to finish.
    [Fact]
    public async Task ExecutorCountChange_TakesEffectWithoutRestart_E2E011()
    {
        await using var host = await HostProcess.StartAsync(new[] { Admin }, initialAdmins: new[] { "admin" });
        await CreateJobAsync(host, "exec-a", """Stage "Work" { Start-Sleep -Seconds 3 }""");
        await CreateJobAsync(host, "exec-b", """Stage "Work" { Write-Host "done" }""");

        var page = await LoggedInPageAsync(host, _pw.Browser);
        await page.Locator("[data-nav=admin]").ClickAsync();
        await page.Locator("[data-testid=admin-tab-settings]").ClickAsync();
        await page.FillAsync("#st-executors", "1");
        await page.Locator("button:has-text('Save')").ClickAsync();

        var aId = await TriggerAsync(host, "exec-a");
        await WaitForStatusAsync(host, aId, "running", TimeSpan.FromSeconds(10));
        var bId = await TriggerAsync(host, "exec-b");

        await page.Locator("[data-nav=queue]").ClickAsync();
        await Expect(page.GetByText("exec-b")).ToBeVisibleAsync();
        await Expect(page.Locator(".badge-status", new() { HasTextString = "Queued" })).ToBeVisibleAsync();

        await page.Locator("[data-nav=admin]").ClickAsync();
        await page.Locator("[data-testid=admin-tab-settings]").ClickAsync();
        await page.FillAsync("#st-executors", "2");
        await page.Locator("button:has-text('Save')").ClickAsync();

        await WaitForStatusAsync(host, aId, "success", TimeSpan.FromSeconds(15));
        await WaitForStatusAsync(host, bId, "success", TimeSpan.FromSeconds(15));

        // Overlap check (mirrors the dispatcher's own ENG-003 test): the two runs only overlap if the
        // settings-screen change actually raised the live executor limit rather than requiring a
        // restart - a stuck executorLimit=1 would have forced them to run back-to-back instead.
        using var aDoc = await HttpJson.ReadJsonAsync(await host.Client.GetAsync($"/api/builds/{aId}"));
        using var bDoc = await HttpJson.ReadJsonAsync(await host.Client.GetAsync($"/api/builds/{bId}"));
        var aStarted = DateTimeOffset.Parse(aDoc.RootElement.GetProperty("startedAt").GetString()!);
        var aFinished = DateTimeOffset.Parse(aDoc.RootElement.GetProperty("finishedAt").GetString()!);
        var bStarted = DateTimeOffset.Parse(bDoc.RootElement.GetProperty("startedAt").GetString()!);
        var bFinished = DateTimeOffset.Parse(bDoc.RootElement.GetProperty("finishedAt").GetString()!);
        Assert.True(aStarted < bFinished && bStarted < aFinished,
            $"expected concurrent execution once executors was raised. A=[{aStarted:o},{aFinished:o}] B=[{bStarted:o},{bFinished:o}]");

        // Fresh navigation (not a live SSE refresh) confirms the queue screen itself reflects the
        // outcome once both builds are done.
        await page.Locator("[data-nav=queue]").ClickAsync();
        await Expect(page.GetByText("Queue is empty.")).ToBeVisibleAsync(new() { Timeout = 10000 });
    }

    // E2E-014: admin force-release from the Resources tab frees the resource (with a confirm dialog)
    // and the waiting build proceeds.
    [Fact]
    public async Task ResourceForceRelease_FreesResourceAndUnblocksWaitingBuild_E2E014()
    {
        await using var host = await HostProcess.StartAsync(new[] { Admin }, initialAdmins: new[] { "admin" });
        await CreateJobAsync(host, "res-holder", """Stage "Work" { Start-Sleep -Seconds 60 }""", new[] { "bench-9" });
        await CreateJobAsync(host, "res-waiter", """Stage "Work" { Write-Host "done" }""", new[] { "bench-9" });
        // An explicit description keeps bench-9 listed even once it's unheld again (the admin API
        // otherwise only lists a resource while it's "described" or currently held - spec §5 F3a
        // "事前定義不要", so an un-described, un-held resource simply has no row at all).
        await HttpJson.PostAsync(host.Client, "/api/admin/resources", new { name = "bench-9", description = "test bench" });

        var holderId = await TriggerAsync(host, "res-holder");
        await WaitForStatusAsync(host, holderId, "running", TimeSpan.FromSeconds(10));
        var waiterId = await TriggerAsync(host, "res-waiter");
        await WaitForStatusAsync(host, waiterId, "waiting", TimeSpan.FromSeconds(10));

        var page = await LoggedInPageAsync(host, _pw.Browser);
        await page.Locator("[data-nav=admin]").ClickAsync();
        await page.Locator("[data-testid=admin-tab-resources]").ClickAsync();

        var resourceRow = page.Locator(".trow", new() { HasText = "bench-9" });
        await Expect(resourceRow).ToContainTextAsync("res-holder");
        var releaseBtn = resourceRow.Locator("button:has-text('Release')");
        await Expect(releaseBtn).ToBeVisibleAsync();

        page.Dialog += (_, dialog) => dialog.AcceptAsync();
        await releaseBtn.ClickAsync();

        // Force-release immediately hands bench-9 to res-waiter (the only build waiting on it), which
        // finishes almost instantly - so "free" may never be observable in between. Wait for res-waiter
        // to actually finish (the real point of E2E-014: the waiting build proceeds), then re-open the
        // Resources tab for a fresh snapshot that should show the resource free again.
        await WaitForStatusAsync(host, waiterId, "success", TimeSpan.FromSeconds(10));

        await page.Locator("[data-nav=admin]").ClickAsync();
        await page.Locator("[data-testid=admin-tab-resources]").ClickAsync();
        var resourceRowAfter = page.Locator(".trow", new() { HasText = "bench-9" });
        await Expect(resourceRowAfter).ToContainTextAsync("free", new() { Timeout = 10000 });
    }
}
