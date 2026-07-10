using CiRunner.E2E.Tests.Support;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace CiRunner.E2E.Tests;

/// <summary>L4 test for cron trigger config (ci-runner-test-spec.md §4 E2E-019, spec §5 F1b "UI: ジョブ
/// 設定に cron 入力と次回発火時刻のプレビュー、ジョブ一覧に次回実行時刻を表示"). Drives the actual admin
/// job-create form: typing a cron expression debounces into a GET /api/admin/cron/preview call that
/// renders the upcoming occurrences, an invalid expression flips the same element into an error, and
/// once saved the job shows its next run time on the main jobs list. The job is created through the UI
/// form itself (not the admin HTTP API like the sibling E2E suites) since this is exactly what
/// E2E-019 is verifying.</summary>
public class CronConfigE2ETests : IAsyncLifetime
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

    // E2E-019: the job form's cron field previews upcoming occurrences as it's typed, flips to an
    // error for a malformed expression, and once a valid one is saved, the main jobs list shows the
    // job's next scheduled run time.
    [Fact]
    public async Task CronField_PreviewsNextRunAndErrorsOnInvalid_AndJobsListShowsNextRunAt_E2E019()
    {
        await using var host = await HostProcess.StartAsync(new[] { Admin }, initialAdmins: new[] { "admin" });
        var page = await LoggedInPageAsync(host, _pw.Browser);

        await page.Locator("[data-nav=admin]").ClickAsync();
        await page.Locator("[data-testid=admin-tab-jobs]").ClickAsync();
        await page.Locator("[data-testid=new-job-btn]").ClickAsync();
        await page.FillAsync("#jf-name", "cron-e2e-job");

        var preview = page.Locator("[data-testid=cron-preview]");
        await Expect(preview).ToBeVisibleAsync();
        await Expect(preview).ToBeEmptyAsync();

        // A valid expression renders an upcoming-occurrence preview (the "->" separator used by the
        // renderer between the expression and its next occurrences).
        await page.FillAsync("#jf-cron", "*/5 * * * *");
        await Expect(preview).ToContainTextAsync("→", new() { Timeout = 5000 });

        // Switching to a malformed expression flips the same element into an error message instead.
        await page.FillAsync("#jf-cron", "not a valid cron");
        await Expect(preview).ToContainTextAsync("invalid format", new() { Timeout = 5000 });
        await Expect(preview).Not.ToContainTextAsync("→");

        // Back to a valid expression and save the job.
        await page.FillAsync("#jf-cron", "0 3 * * *");
        await Expect(preview).ToContainTextAsync("→", new() { Timeout = 5000 });
        await page.Locator("[data-testid=save-job-btn]").ClickAsync();
        await Expect(page.Locator("[data-testid='admin-job-row-cron-e2e-job']")).ToBeVisibleAsync(new() { Timeout = 10000 });

        // The main jobs list surfaces the same job's next scheduled run time.
        await page.Locator("[data-nav=jobs]").ClickAsync();
        var jobRow = page.Locator("[data-testid='job-row-cron-e2e-job']");
        await Expect(jobRow).ToBeVisibleAsync(new() { Timeout = 10000 });
        var nextRun = jobRow.Locator("[data-testid=next-run]");
        await Expect(nextRun).ToBeVisibleAsync(new() { Timeout = 10000 });
        await Expect(nextRun).ToContainTextAsync("Next run:");
    }

    // E2E-019 (companion): a job with no cron schedule shows no next-run element on the jobs list.
    [Fact]
    public async Task JobWithNoCronSchedule_ShowsNoNextRunOnJobsList_E2E019()
    {
        await using var host = await HostProcess.StartAsync(new[] { Admin }, initialAdmins: new[] { "admin" });
        await HttpJson.PostAsync(host.Client, "/api/login", new { username = "admin", password = "admin123" });
        var createRes = await HttpJson.PostAsync(host.Client, "/api/admin/jobs", new { name = "no-cron-e2e-job" });
        createRes.EnsureSuccessStatusCode();
        File.WriteAllText(Path.Combine(host.Root, "jobs", "no-cron-e2e-job", "pipeline.cipipe"), """Stage "Work" { Write-Host "done" }""");

        var page = await LoggedInPageAsync(host, _pw.Browser);
        var jobRow = page.Locator("[data-testid='job-row-no-cron-e2e-job']");
        await Expect(jobRow).ToBeVisibleAsync(new() { Timeout = 10000 });
        await Expect(jobRow.Locator("[data-testid=next-run]")).ToHaveCountAsync(0);
    }
}
