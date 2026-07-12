using CiRunner.E2E.Tests.Support;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace CiRunner.E2E.Tests;

/// <summary>L4 browser E2E tests (ci-runner-test-spec.md §4). Each test launches a real CiRunner.Host
/// subprocess and drives it with headless Chromium, exercising the actual login form and UI - not the
/// API directly - so a regression in the front-end JS itself would be caught here.</summary>
public class LoginAndJobE2ETests : IAsyncLifetime
{
    private static readonly TestLocalUser Admin = new("admin", "admin123", "admin");
    private PlaywrightFixture _pw = null!;

    public async Task InitializeAsync()
    {
        _pw = new PlaywrightFixture();
        await _pw.InitializeAsync();
    }

    public async Task DisposeAsync() => await _pw.DisposeAsync();

    private static async Task LoginAsync(IPage page, HostProcess host, string username, string password)
    {
        await page.GotoAsync(host.BaseUrl);
        await page.ClickAsync("#signin-btn");
        await page.FillAsync("#login-username", username);
        await page.FillAsync("#login-password", password);
        await page.ClickAsync("#login-submit");
    }

    // E2E-001: failed login shows an error message; successful login shows the username.
    [Fact]
    public async Task Login_FailureShowsError_SuccessShowsUsername_E2E001()
    {
        await using var host = await HostProcess.StartAsync(new[] { Admin }, initialAdmins: new[] { "admin" });
        await using var context = await _pw.Browser.NewContextAsync();
        var page = await context.NewPageAsync();

        await LoginAsync(page, host, "admin", "wrong-password");
        await Expect(page.Locator("#login-error")).ToBeVisibleAsync();
        await Expect(page.Locator("#login-error")).ToContainTextAsync("Invalid username or password");
        await Expect(page.Locator("#shell")).ToBeHiddenAsync();

        await page.FillAsync("#login-password", "admin123");
        await page.ClickAsync("#login-submit");
        await Expect(page.Locator("#user-name")).ToContainTextAsync("admin");
        await Expect(page.Locator("#shell")).ToBeVisibleAsync();
    }

    // E2E-002: admin creates a job via the admin UI, triggers it manually, and the build history
    // shows Success - the full vertical slice from UI form to executed pipeline.cipipe.
    [Fact]
    public async Task AdminCreatesJob_TriggersManually_BuildShowsSuccess_E2E002()
    {
        await using var host = await HostProcess.StartAsync(new[] { Admin }, initialAdmins: new[] { "admin" });
        await using var context = await _pw.Browser.NewContextAsync();
        var page = await context.NewPageAsync();

        await LoginAsync(page, host, "admin", "admin123");
        await Expect(page.Locator("#user-name")).ToContainTextAsync("admin");

        await page.Locator("[data-nav=admin]").ClickAsync();
        await page.Locator("[data-testid=admin-tab-jobs]").ClickAsync();
        await page.Locator("[data-testid=new-job-btn]").ClickAsync();
        await page.FillAsync("#jf-name", "e2e-job");
        await page.Locator("[data-testid=save-job-btn]").ClickAsync();
        await Expect(page.Locator("[data-testid='admin-job-row-e2e-job']")).ToBeVisibleAsync();

        await page.Locator("[data-nav=jobs]").ClickAsync();
        await page.Locator("[data-testid='job-row-e2e-job']").ClickAsync();
        await page.Locator("[data-testid=run-now-btn]").ClickAsync();

        await Expect(page.Locator(".badge-status")).ToContainTextAsync("Success", new() { Timeout = 20000 });
    }
}
