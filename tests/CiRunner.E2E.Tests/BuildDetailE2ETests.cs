using CiRunner.E2E.Tests.Support;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace CiRunner.E2E.Tests;

/// <summary>L4 tests for the build detail page (ci-runner-test-spec.md §4 E2E-003/006/007). Jobs are
/// created/triggered via the admin HTTP API (fast, deterministic) since these scenarios verify build
/// detail rendering, not the job-creation form itself (covered by E2E-002).</summary>
public class BuildDetailE2ETests : IAsyncLifetime
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

    // E2E-003: while a build is running, its live log grows over time (two snapshots, second > first),
    // rather than the UI showing a single static banner.
    [Fact]
    public async Task LiveLog_GrowsWhileBuildIsRunning_E2E003()
    {
        await using var host = await HostProcess.StartAsync(new[] { Admin }, initialAdmins: new[] { "admin" });
        var buildId = await CreateAndTriggerJobAsync(host, "live-log-job", """
            Stage "Work" {
                for ($i = 1; $i -le 6; $i++) {
                    Write-Host "line $i"
                    Start-Sleep -Milliseconds 500
                }
            }
            """);

        var page = await LoggedInPageAsync(host, _pw.Browser);
        await page.GotoAsync(host.BaseUrl + $"#/builds/{buildId}");
        await page.Locator(".stephead").First.ClickAsync(); // expand the (auto-running) step

        await Expect(page.Locator(".logline").First).ToBeVisibleAsync(new() { Timeout = 10000 });
        var firstCount = await page.Locator(".logline").CountAsync();
        await Task.Delay(1500);
        var secondCount = await page.Locator(".logline").CountAsync();

        Assert.True(secondCount > firstCount, $"expected the live log to grow (first={firstCount}, second={secondCount})");
    }

    // E2E-006: JUnit results render as a table with suite/case/status/duration, and a failed case's
    // message is shown.
    [Fact]
    public async Task TestResultsTab_ShowsJUnitTableWithFailureMessage_E2E006()
    {
        await using var host = await HostProcess.StartAsync(new[] { Admin }, initialAdmins: new[] { "admin" });
        var buildId = await CreateAndTriggerJobAsync(host, "junit-job", """
            Stage "Test" {
                @'
            <testsuite name="demo">
              <testcase classname="demo" name="it_passes" time="0.01"/>
              <testcase classname="demo" name="it_fails" time="0.02"><failure message="boom">stack trace here</failure></testcase>
            </testsuite>
            '@ | Set-Content results.xml
                Register-JUnit "results.xml"
            }
            """);

        var page = await LoggedInPageAsync(host, _pw.Browser);
        await page.GotoAsync(host.BaseUrl + $"#/builds/{buildId}");

        await Expect(page.Locator("[data-testid=build-tab-tests]")).ToBeVisibleAsync(new() { Timeout = 15000 });
        await page.Locator("[data-testid=build-tab-tests]").ClickAsync();

        await Expect(page.GetByText("it_passes")).ToBeVisibleAsync();
        await Expect(page.GetByText("it_fails")).ToBeVisibleAsync();
        await Expect(page.GetByText("stack trace here")).ToBeVisibleAsync();
    }

    // E2E-007: an artifact's download link serves the exact bytes the pipeline registered.
    [Fact]
    public async Task ArtifactsTab_DownloadLinkServesExactContent_E2E007()
    {
        await using var host = await HostProcess.StartAsync(new[] { Admin }, initialAdmins: new[] { "admin" });
        const string artifactContent = "hello artifact E2E-007";
        var buildId = await CreateAndTriggerJobAsync(host, "artifact-job", $$"""
            Stage "Build" {
                "{{artifactContent}}" | Set-Content out.txt -NoNewline
                Register-Artifact "out.txt"
            }
            """);

        var page = await LoggedInPageAsync(host, _pw.Browser);
        await page.GotoAsync(host.BaseUrl + $"#/builds/{buildId}");

        await Expect(page.Locator("[data-testid=build-tab-artifacts]")).ToBeVisibleAsync(new() { Timeout = 15000 });
        await page.Locator("[data-testid=build-tab-artifacts]").ClickAsync();

        var downloadLink = page.Locator("a:has-text('Download')");
        await Expect(downloadLink).ToBeVisibleAsync();
        var href = await downloadLink.GetAttributeAsync("href");
        Assert.NotNull(href);

        var downloaded = await host.Client.GetStringAsync(href);
        Assert.Equal(artifactContent, downloaded);
    }

    // E2E-008: the Abort button on a running build's detail page marks it Aborted and the UI reflects
    // the status change without a manual page reload (driven by the SSE build-finished event).
    [Fact]
    public async Task AbortButton_StopsRunningBuild_E2E008()
    {
        await using var host = await HostProcess.StartAsync(new[] { Admin }, initialAdmins: new[] { "admin" });
        var buildId = await CreateAndTriggerJobAsync(host, "abort-btn-job", """
            Stage "Work" { Start-Sleep -Seconds 30 }
            """);

        var page = await LoggedInPageAsync(host, _pw.Browser);
        await page.GotoAsync(host.BaseUrl + $"#/builds/{buildId}");

        var abortBtn = page.Locator("[data-testid=abort-btn]");
        await Expect(abortBtn).ToBeVisibleAsync(new() { Timeout = 15000 });
        page.Dialog += (_, dialog) => dialog.AcceptAsync();
        await abortBtn.ClickAsync();

        await Expect(page.Locator(".badge-status", new() { HasTextString = "Aborted" })).ToBeVisibleAsync(new() { Timeout = 15000 });
    }

    // E2E-009: Rebuild queues a new build for the same job/SHA, tagged trigger=rebuild, and the UI
    // navigates to the new build's own detail page.
    [Fact]
    public async Task RebuildButton_QueuesNewBuildWithRebuildTrigger_E2E009()
    {
        await using var host = await HostProcess.StartAsync(new[] { Admin }, initialAdmins: new[] { "admin" });
        var buildId = await CreateAndTriggerJobAsync(host, "rebuild-btn-job", """
            Stage "Work" { Write-Host "done" }
            """);

        var page = await LoggedInPageAsync(host, _pw.Browser);
        await page.GotoAsync(host.BaseUrl + $"#/builds/{buildId}");

        var rebuildBtn = page.Locator("[data-testid=rebuild-btn]");
        await Expect(rebuildBtn).ToBeVisibleAsync(new() { Timeout = 15000 });
        await rebuildBtn.ClickAsync();

        await Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex(@"#/builds/(?!" + buildId + @"$)\d+$"), new() { Timeout = 15000 });
        await Expect(page.Locator(".mono.sub").First).ToContainTextAsync("rebuild", new() { Timeout = 15000 });
    }
}
