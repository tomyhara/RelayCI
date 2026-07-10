using CiRunner.E2E.Tests.Support;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace CiRunner.E2E.Tests;

/// <summary>L4 test for the parameterized manual-trigger flow (ci-runner-test-spec.md §4 E2E-017,
/// spec §5 F1a/F5). A job with declared parameters prompts a form on Run now (defaults prefilled,
/// required fields validated client-side); the used parameters are stored, shown on the build detail
/// page, and injected as environment variables into the pipeline. A parameterless job still triggers
/// one-click with no form. Jobs are created via the admin HTTP API (like the sibling E2E suites) since
/// this verifies the trigger form, not the job-creation form (covered by E2E-002).</summary>
public class ParameterTriggerE2ETests : IAsyncLifetime
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
        return page;
    }

    private static async Task CreateJobAsync(HostProcess host, string name, string pipeline, object[]? parameters = null)
    {
        await HttpJson.PostAsync(host.Client, "/api/login", new { username = "admin", password = "admin123" });
        var createRes = await HttpJson.PostAsync(host.Client, "/api/admin/jobs", new { name, parameters = parameters ?? Array.Empty<object>() });
        createRes.EnsureSuccessStatusCode();
        File.WriteAllText(Path.Combine(host.Root, "jobs", name, "pipeline.cipipe"), pipeline);
    }

    private static async Task WaitForStatusAsync(HostProcess host, long buildId, string status, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        string? last = null;
        while (DateTime.UtcNow < deadline)
        {
            using var doc = await HttpJson.ReadJsonAsync(await host.Client.GetAsync($"/api/builds/{buildId}"));
            last = doc.RootElement.GetProperty("status").GetString();
            if (last == status)
            {
                return;
            }
            await Task.Delay(100);
        }
        throw new TimeoutException($"build {buildId} did not reach status '{status}' in time (last seen: '{last}')");
    }

    private static async Task<long> WaitForLatestBuildIdAsync(HostProcess host, string jobName, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            using var doc = await HttpJson.ReadJsonAsync(await host.Client.GetAsync($"/api/jobs/{jobName}/builds"));
            if (doc.RootElement.GetArrayLength() > 0)
            {
                return doc.RootElement[0].GetProperty("id").GetInt64();
            }
            await Task.Delay(100);
        }
        throw new TimeoutException($"no build appeared for job '{jobName}' in time");
    }

    private static async Task<int> BuildCountAsync(HostProcess host, string jobName)
    {
        using var doc = await HttpJson.ReadJsonAsync(await host.Client.GetAsync($"/api/jobs/{jobName}/builds"));
        return doc.RootElement.GetArrayLength();
    }

    /// <summary>Fetches the build log until it contains every marker (or times out). The log endpoint
    /// serves a completed build's static file, but if the fetch lands in the brief window between a
    /// build reaching 'success' and the live-log hub tearing down, it can instead return a partial
    /// in-flight SSE snapshot - a quick retry then sees the finalized full log.</summary>
    private static async Task<string> WaitForLogContainingAsync(HostProcess host, long buildId, string[] markers, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var log = "";
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                log = await host.Client.GetStringAsync($"/api/builds/{buildId}/log/stream");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                await Task.Delay(100); // transient hiccup under parallel load; retry
                continue;
            }
            if (markers.All(log.Contains))
            {
                return log;
            }
            await Task.Delay(100);
        }
        return log; // return the last snapshot so the caller's Assert produces a helpful diff
    }

    // E2E-017: parameterized manual trigger - the form appears with the declared default prefilled, a
    // missing required parameter blocks submit with an inline error (and creates no build), and once
    // filled the build runs, stores the used parameters (shown on build detail), and injects them as
    // environment variables into the pipeline.
    [Fact]
    public async Task ParameterizedTrigger_ShowsFormValidatesRequiredAndFlowsThrough_E2E017()
    {
        await using var host = await HostProcess.StartAsync(new[] { Admin }, initialAdmins: new[] { "admin" });
        await CreateJobAsync(host, "param-job",
            """
            Stage "Echo" {
                Write-Host "GOT_TARGET=$env:Target"
                Write-Host "GOT_REF=$env:Ref"
            }
            """,
            new object[]
            {
                new { Name = "Target", Default = "prod", Description = "deployment target", Required = false },
                new { Name = "Ref", Default = (string?)null, Description = "git ref to build", Required = true },
            });

        var page = await LoggedInPageAsync(host, _pw.Browser);
        await page.GotoAsync(host.BaseUrl + "#/jobs/param-job");

        // Run now on a job with parameters opens the form with the declared default prefilled. Wait for
        // the button to settle after the (hashchange-driven) history render before clicking.
        var runNow = page.Locator("[data-testid=run-now-btn]");
        await Expect(runNow).ToBeVisibleAsync();
        await runNow.ClickAsync();
        await Expect(page.Locator("[data-testid=param-form]")).ToBeVisibleAsync();
        await Expect(page.Locator("[data-testid=param-input-Target]")).ToHaveValueAsync("prod");
        await Expect(page.Locator("[data-testid=param-input-Ref]")).ToHaveValueAsync("");

        // Required 'Ref' is empty: submit is blocked with an inline error, and no build is created.
        await page.Locator("[data-testid=param-run-btn]").ClickAsync();
        await Expect(page.Locator("[data-testid=param-form]")).ToBeVisibleAsync();
        await Expect(page.GetByText("Ref is required.")).ToBeVisibleAsync();
        Assert.Equal(0, await BuildCountAsync(host, "param-job"));

        // Fill the required field and run for real; the form closes.
        await page.FillAsync("[data-testid=param-input-Ref]", "main");
        await page.Locator("[data-testid=param-run-btn]").ClickAsync();
        await Expect(page.Locator("[data-testid=param-form]")).ToHaveCountAsync(0);

        var buildId = await WaitForLatestBuildIdAsync(host, "param-job", TimeSpan.FromSeconds(10));
        await WaitForStatusAsync(host, buildId, "success", TimeSpan.FromSeconds(20));

        // Build detail shows both used parameter values (spec §5 F1a "ビルド詳細に表示").
        await page.GotoAsync(host.BaseUrl + $"#/builds/{buildId}");
        var chips = page.Locator("[data-testid=build-params]");
        await Expect(chips).ToBeVisibleAsync(new() { Timeout = 15000 });
        await Expect(chips).ToContainTextAsync("Target");
        await Expect(chips).ToContainTextAsync("prod");
        await Expect(chips).ToContainTextAsync("Ref");
        await Expect(chips).ToContainTextAsync("main");

        // The values actually reached the pipeline as environment variables (spec §5 F1a "宣言名その
        // ままの環境変数として注入"): the completed build's log carries the echoed markers.
        var log = await WaitForLogContainingAsync(host, buildId,
            new[] { "GOT_TARGET=prod", "GOT_REF=main" }, TimeSpan.FromSeconds(10));
        Assert.Contains("GOT_TARGET=prod", log);
        Assert.Contains("GOT_REF=main", log);
    }

    // E2E-017 (companion): a job with NO declared parameters keeps the immediate one-click behavior -
    // Run now triggers straight away with no form (the invariant existing E2E-002/013 depend on).
    [Fact]
    public async Task ParameterlessJob_TriggersImmediatelyWithNoForm_E2E017()
    {
        await using var host = await HostProcess.StartAsync(new[] { Admin }, initialAdmins: new[] { "admin" });
        await CreateJobAsync(host, "plain-job", """Stage "Work" { Write-Host "done" }""");

        var page = await LoggedInPageAsync(host, _pw.Browser);
        await page.GotoAsync(host.BaseUrl + "#/jobs/plain-job");
        var runNow = page.Locator("[data-testid=run-now-btn]");
        await Expect(runNow).ToBeVisibleAsync();
        await runNow.ClickAsync();

        // No parameter form is shown, and the build runs to Success on its own.
        await Expect(page.Locator("[data-testid=param-form]")).ToHaveCountAsync(0);
        await Expect(page.Locator(".badge-status")).ToContainTextAsync("Success", new() { Timeout = 20000 });
    }
}
