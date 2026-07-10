using System.Text;
using System.Text.Json;
using CiRunner.E2E.Tests.Support;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace CiRunner.E2E.Tests;

/// <summary>L4 test for hook management and run history (ci-runner-test-spec.md §4 E2E-018; spec
/// §5 F1/F6 "フック実行履歴" - each delivery must be traceable to the builds it launched). The hook
/// is created through the actual Admin &gt; Hooks form; the delivery is a real POST to
/// /api/webhook/{name}; the run-history view and its triggered-build link are driven in the browser.
/// The target job is created via the admin HTTP API (job creation UI is covered by E2E-002).</summary>
public class HookHistoryE2ETests : IAsyncLifetime
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

    /// <summary>Polls the admin hook-runs API until the newest run for the hook is complete and has
    /// attributed at least one triggered build, returning that build id. Completing without a build id
    /// would mean the Start-CiJob → hookRunId → AppendTriggeredBuild chain is broken again.</summary>
    private static async Task<long> WaitForTriggeredBuildAsync(HostProcess host, string hookName, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        string? lastSeen = null;
        while (DateTime.UtcNow < deadline)
        {
            var res = await host.Client.GetAsync($"/api/admin/hooks/{hookName}/runs");
            if (res.IsSuccessStatusCode)
            {
                using var doc = await HttpJson.ReadJsonAsync(res);
                if (doc.RootElement.GetArrayLength() > 0)
                {
                    var run = doc.RootElement[0];
                    var status = run.GetProperty("status").GetString();
                    var triggered = JsonSerializer.Deserialize<List<long>>(run.GetProperty("triggeredBuilds").GetString() ?? "[]") ?? new();
                    lastSeen = $"status={status}, triggeredBuilds=[{string.Join(",", triggered)}]";
                    if (status == "success" && triggered.Count > 0)
                    {
                        return triggered[0];
                    }
                    if (status is "failed" or "timeout")
                    {
                        throw new InvalidOperationException($"hook run for '{hookName}' ended {status} instead of triggering a build");
                    }
                }
            }
            await Task.Delay(200);
        }
        throw new TimeoutException($"hook '{hookName}' run did not complete with a triggered build in time (last seen: {lastSeen ?? "no runs"})");
    }

    // E2E-018: admin creates a hook (no secret = HMAC skipped) via the Admin > Hooks form, the
    // handler script is replaced with one that calls Start-CiJob for a prepared job, a webhook
    // payload is POSTed with X-GitHub-Delivery/X-GitHub-Event headers, and the hook's run history
    // in the UI must show the delivery row with a working link to the triggered build's detail page.
    [Fact]
    public async Task HookCreatedInUI_WebhookRun_HistoryShowsRowAndTriggeredBuildLink_E2E018()
    {
        await using var host = await HostProcess.StartAsync(new[] { Admin }, initialAdmins: new[] { "admin" });
        await CreateJobAsync(host, "hook-target-job", """Stage "Work" { Write-Host "triggered by hook" }""");

        var page = await LoggedInPageAsync(host, _pw.Browser);

        // Create the hook through the real admin form (name, no secret, timeout, enabled).
        await page.Locator("[data-nav=admin]").ClickAsync();
        await page.Locator("[data-testid=admin-tab-hooks]").ClickAsync();
        await page.Locator("button:has-text('New Hook')").ClickAsync();
        await page.FillAsync("#hf-name", "e2e-hook");
        await page.FillAsync("#hf-timeout", "30");
        // #hf-secret deliberately left blank: create WITHOUT a secret so the POST below needs no HMAC.
        await Expect(page.Locator("#hf-enabled")).ToBeCheckedAsync();
        await page.Locator("button:has-text('Save')").ClickAsync();
        var hookRow = page.Locator(".trow", new() { HasText = "e2e-hook" });
        await Expect(hookRow).ToBeVisibleAsync();
        await Expect(hookRow).ToContainTextAsync("(none)"); // secret column confirms no HMAC secret was stored

        // Hook creation wrote the template handler to hooks/e2e-hook.cipipe under the host root;
        // replace it with one that actually starts the target job.
        var handlerPath = Path.Combine(host.Root, "hooks", "e2e-hook.cipipe");
        Assert.True(File.Exists(handlerPath), $"hook creation should have written the handler template at {handlerPath}");
        File.WriteAllText(handlerPath, """Start-CiJob "hook-target-job" """);

        // Deliver a payload exactly as GHES would (spec §5 F1) - unauthenticated endpoint, HMAC
        // skipped because the hook has no secret.
        var delivery = new HttpRequestMessage(HttpMethod.Post, "/api/webhook/e2e-hook")
        {
            Content = new StringContent("""{"ref":"refs/heads/main","after":"abc123"}""", Encoding.UTF8, "application/json"),
        };
        delivery.Headers.Add("X-GitHub-Delivery", Guid.NewGuid().ToString());
        delivery.Headers.Add("X-GitHub-Event", "push");
        var deliveryRes = await host.Client.SendAsync(delivery);
        Assert.Equal(System.Net.HttpStatusCode.OK, deliveryRes.StatusCode);

        // Handler startup is a real powershell.exe: wait via API until the run completed AND the
        // triggered build is attributed, then let that build finish so its detail page is stable.
        var buildId = await WaitForTriggeredBuildAsync(host, "e2e-hook", TimeSpan.FromSeconds(30));
        await WaitForStatusAsync(host, buildId, "success", TimeSpan.FromSeconds(20));

        // Open the hook's run history in the UI (admin page isn't SSE-refreshed, so re-open for a
        // fresh snapshot) and follow the triggered-build link.
        await page.Locator("[data-nav=admin]").ClickAsync();
        await page.Locator("[data-testid=admin-tab-hooks]").ClickAsync();
        await page.Locator(".trow", new() { HasText = "e2e-hook" }).Locator("button:has-text('History')").ClickAsync();

        var runRow = page.Locator(".trow", new() { HasText = "push" });
        await Expect(runRow).ToBeVisibleAsync(new() { Timeout = 10000 });
        await Expect(runRow.Locator(".badge-status")).ToContainTextAsync("Success");

        var buildLink = runRow.Locator(".link", new() { HasTextString = $"#{buildId}" });
        await Expect(buildLink).ToBeVisibleAsync();
        await buildLink.ClickAsync();

        // The link must land on that build's own detail page.
        await Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex($"#/builds/{buildId}$"), new() { Timeout = 10000 });
        await Expect(page.Locator("#build-header")).ToContainTextAsync("hook-target-job", new() { Timeout = 10000 });
        await Expect(page.Locator("#build-header")).ToContainTextAsync("hook"); // trigger source shown as hook
    }
}
