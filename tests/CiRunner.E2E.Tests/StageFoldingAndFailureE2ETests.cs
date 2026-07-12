using CiRunner.E2E.Tests.Support;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace CiRunner.E2E.Tests;

/// <summary>L4 tests for stage folding and failed-build rendering on the build detail page
/// (ci-runner-test-spec.md §4 E2E-004/E2E-005; spec §5 F5 "ステップごとの折りたたみ"). Jobs are
/// created/triggered via the admin HTTP API (fast, deterministic) since these scenarios verify build
/// detail rendering, not the job-creation form itself (covered by E2E-002).</summary>
public class StageFoldingAndFailureE2ETests : IAsyncLifetime
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
        // Let boot()'s initial async renderRoute('/jobs') finish before the caller hash-navigates
        // elsewhere: its fetches resolve on their own schedule and the LAST writer wins
        // main.innerHTML, so navigating mid-flight lets a stale jobs-list render overwrite the
        // caller's freshly rendered view (observed as a real flake: build detail wiped, .step gone).
        await Expect(page.Locator(".statgrid")).ToBeVisibleAsync(new() { Timeout = 10000 });
        return page;
    }

    private static async Task<long> CreateAndTriggerJobAsync(HostProcess host, string name, string pipeline)
    {
        await HttpJson.PostAsync(host.Client, "/api/login", new { username = "admin", password = "admin123" });
        var createRes = await HttpJson.PostAsync(host.Client, "/api/admin/jobs", new { name });
        createRes.EnsureSuccessStatusCode();
        File.WriteAllText(Path.Combine(host.Root, "jobs", name, "pipeline.cipipe"), pipeline);

        var triggerRes = await host.Client.PostAsync($"/api/jobs/{name}/trigger", null);
        triggerRes.EnsureSuccessStatusCode();
        using var triggered = await HttpJson.ReadJsonAsync(triggerRes);
        return triggered.RootElement.GetProperty("id").GetInt64();
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

    // E2E-004: folding units are based on log byte offsets (build_steps.log_offset_start/end),
    // not banner-text pattern matching. Each stage prints a unique marker line; expanding one
    // stage's .steplog must show only that stage's marker, never the other stage's. The 500ms
    // pauses around each marker give the control-file tailer (50ms poll, ControlFileTailer.cs)
    // room to record each stage-start/stage-end offset before the next stage's own output starts
    // landing in the shared log buffer - without that gap, a trivial near-instant pipeline risks
    // both stages' output racing into the log before the tailer captures a boundary between them,
    // which would make this test flaky for reasons unrelated to what it's actually verifying.
    [Fact]
    public async Task StageFolding_ExpandingEachStepShowsOnlyItsOwnMarkers_E2E004()
    {
        await using var host = await HostProcess.StartAsync(new[] { Admin }, initialAdmins: new[] { "admin" });
        var buildId = await CreateAndTriggerJobAsync(host, "fold-job", """
            Stage "StageOne" {
                Start-Sleep -Milliseconds 500
                Write-Host "MARKER_STAGE_ONE_abc"
                Start-Sleep -Milliseconds 500
            }
            Stage "StageTwo" {
                Start-Sleep -Milliseconds 500
                Write-Host "MARKER_STAGE_TWO_xyz"
                Start-Sleep -Milliseconds 500
            }
            """);
        await WaitForStatusAsync(host, buildId, "success", TimeSpan.FromSeconds(20));

        var page = await LoggedInPageAsync(host, _pw.Browser);
        await page.GotoAsync(host.BaseUrl + $"#/builds/{buildId}");

        var steps = page.Locator(".step");
        await Expect(steps).ToHaveCountAsync(2, new() { Timeout = 15000 });

        // Expand stage 1 only: its own log must contain its own marker and not stage 2's.
        var step1 = steps.Nth(0);
        await step1.Locator(".stephead").ClickAsync();
        var step1Log = step1.Locator(".steplog");
        await Expect(step1Log.Locator(".logline").First).ToBeVisibleAsync(new() { Timeout = 10000 });
        var step1Text = await step1Log.InnerTextAsync();
        Assert.Contains("MARKER_STAGE_ONE_abc", step1Text);
        Assert.DoesNotContain("MARKER_STAGE_TWO_xyz", step1Text);

        // Now expand stage 2 as well: the reverse must hold for its own log.
        var step2 = page.Locator(".step").Nth(1);
        await step2.Locator(".stephead").ClickAsync();
        var step2Log = step2.Locator(".steplog");
        await Expect(step2Log.Locator(".logline").First).ToBeVisibleAsync(new() { Timeout = 10000 });
        var step2Text = await step2Log.InnerTextAsync();
        Assert.Contains("MARKER_STAGE_TWO_xyz", step2Text);
        Assert.DoesNotContain("MARKER_STAGE_ONE_abc", step2Text);
    }

    // E2E-005: a stage that throws fails the build. The build detail page must show the overall
    // Failed status, visually distinguish the failed stage from a normal one, and surface the
    // throw's message as-is (Stage's catch block in CiRunner.psm1 records $_.Exception.Message
    // verbatim, only truncating past 4KB - DSL-003), which renderStep (index.html) renders in an
    // error box beneath the failed step regardless of whether that step's log is expanded.
    [Fact]
    public async Task FailedBuild_ShowsFailedStatusDistinguishedStageAndErrorMessage_E2E005()
    {
        await using var host = await HostProcess.StartAsync(new[] { Admin }, initialAdmins: new[] { "admin" });
        var buildId = await CreateAndTriggerJobAsync(host, "fail-job", """
            Stage "Setup" { Write-Host "ok" }
            Stage "Boom" { throw "boom-E2E005-message" }
            """);
        await WaitForStatusAsync(host, buildId, "failed", TimeSpan.FromSeconds(20));

        var page = await LoggedInPageAsync(host, _pw.Browser);
        await page.GotoAsync(host.BaseUrl + $"#/builds/{buildId}");

        // Anchoring on the step count first also guarantees the badge assertion below is evaluated
        // against the build detail view, not the jobs list (which shows its own Failed badge).
        await Expect(page.Locator(".step")).ToHaveCountAsync(2, new() { Timeout = 15000 });
        await Expect(page.Locator(".badge-status").First).ToContainTextAsync("Failed");

        // Visual distinction: renderStep sets a failed step's border color to the failed-status
        // token, unlike a normal/successful step's default border.
        var boomStep = page.Locator(".step", new() { HasText = "Boom" });
        var boomStyle = await boomStep.GetAttributeAsync("style");
        Assert.Contains("var(--st-failed)", boomStyle ?? "");

        var setupStep = page.Locator(".step", new() { HasText = "Setup" });
        var setupStyle = await setupStep.GetAttributeAsync("style");
        Assert.DoesNotContain("st-failed", setupStyle ?? "");

        // Error message text visible on the page (build_steps.error, from GET /api/builds/{id}).
        await Expect(page.GetByText("boom-E2E005-message")).ToBeVisibleAsync();
    }
}
