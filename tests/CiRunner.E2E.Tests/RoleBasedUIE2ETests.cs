using CiRunner.E2E.Tests.Support;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace CiRunner.E2E.Tests;

/// <summary>L4 tests for role-gated UI (ci-runner-test-spec.md §4 E2E-012/013).</summary>
public class RoleBasedUIE2ETests : IAsyncLifetime
{
    private static readonly TestLocalUser Admin = new("admin", "admin123", "admin");
    private static readonly TestLocalUser Viewer = new("vwr", "vwr123", "viewer");

    private PlaywrightFixture _pw = null!;

    public async Task InitializeAsync()
    {
        _pw = new PlaywrightFixture();
        await _pw.InitializeAsync();
    }

    public async Task DisposeAsync() => await _pw.DisposeAsync();

    private static async Task<IPage> LoginAsync(HostProcess host, IBrowser browser, string username, string password)
    {
        var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync(host.BaseUrl);
        await page.FillAsync("#login-username", username);
        await page.FillAsync("#login-password", password);
        await page.ClickAsync("#login-submit");
        await Expect(page.Locator("#user-name")).ToContainTextAsync(username);
        return page;
    }

    // E2E-012: a viewer sees neither the Admin nav entry nor any trigger button, and a direct
    // hash-navigation to #/admin is refused client-side rather than rendering the settings UI.
    [Fact]
    public async Task Viewer_DoesNotSeeAdminNavOrTriggerButtons_AndAdminUrlIsRefused_E2E012()
    {
        await using var host = await HostProcess.StartAsync(new[] { Admin, Viewer }, initialAdmins: new[] { "admin" });
        await HttpJson.PostAsync(host.Client, "/api/login", new { username = "admin", password = "admin123" });
        await HttpJson.PostAsync(host.Client, "/api/admin/jobs", new { name = "viewer-visible-job" });

        var page = await LoginAsync(host, _pw.Browser, "vwr", "vwr123");

        await Expect(page.Locator("[data-nav=admin]")).ToBeHiddenAsync();
        await Expect(page.Locator("[data-testid=run-now-btn]")).ToHaveCountAsync(0);

        await page.Locator("[data-nav=jobs]").ClickAsync();
        await Expect(page.Locator("[data-testid='job-row-viewer-visible-job']")).ToBeVisibleAsync();
        await Expect(page.Locator("[data-testid=run-now-btn]")).ToHaveCountAsync(0);

        await page.EvaluateAsync("navigate('/admin')");
        await Expect(page.GetByText("Admin access required.")).ToBeVisibleAsync();
        await Expect(page.Locator("[data-testid=admin-tab-jobs]")).ToHaveCountAsync(0);
    }

    // E2E-013: admin grants operator to a brand-new username (no prior login needed - spec §5 F6
    // "ユーザー名直接入力、対象の事前ログイン不要"); that user can then log in and trigger a build.
    [Fact]
    public async Task AdminGrantsOperatorRole_GrantedUserCanThenTriggerABuild_E2E013()
    {
        await using var host = await HostProcess.StartAsync(new[] { Admin, new TestLocalUser("newop", "newop123", "operator") }, initialAdmins: new[] { "admin" });
        var adminPage = await LoginAsync(host, _pw.Browser, "admin", "admin123");
        await HttpJson.PostAsync(host.Client, "/api/login", new { username = "admin", password = "admin123" });
        await HttpJson.PostAsync(host.Client, "/api/admin/jobs", new { name = "grant-job" });

        await adminPage.Locator("[data-nav=admin]").ClickAsync();
        await adminPage.Locator("[data-testid=admin-tab-users]").ClickAsync();
        await Expect(adminPage.Locator("#au-username")).ToBeVisibleAsync();
        await adminPage.FillAsync("#au-username", "newop");
        await adminPage.SelectOptionAsync("#au-role", "operator");
        await adminPage.Locator("[data-testid=assign-role-btn]").ClickAsync();
        await Expect(adminPage.GetByText("newop")).ToBeVisibleAsync();

        var operatorPage = await LoginAsync(host, _pw.Browser, "newop", "newop123");
        await operatorPage.Locator("[data-nav=jobs]").ClickAsync();
        await operatorPage.Locator("[data-testid='job-row-grant-job']").ClickAsync();
        await operatorPage.Locator("[data-testid=run-now-btn]").ClickAsync();

        await Expect(operatorPage.Locator(".badge-status")).ToContainTextAsync("Success", new() { Timeout = 20000 });
    }
}
