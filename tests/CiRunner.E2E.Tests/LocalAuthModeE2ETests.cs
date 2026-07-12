using CiRunner.E2E.Tests.Support;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace CiRunner.E2E.Tests;

/// <summary>L4 browser test for auth.mode="local" (spec §9/§5 F6): the admin Users tab's local-accounts
/// panel only renders under mode=local, and a create round-trips through the real UI. Kept in its own
/// test class (rather than folded into RoleBasedUIE2ETests) per the project's guidance to run new E2E
/// classes filtered given the known flakiness of the full parallel E2E suite.</summary>
public class LocalAuthModeE2ETests : IAsyncLifetime
{
    private static readonly TestLocalUser Admin = new("admin", "admin123", "admin");
    private PlaywrightFixture _pw = null!;

    public async Task InitializeAsync()
    {
        _pw = new PlaywrightFixture();
        await _pw.InitializeAsync();
    }

    public async Task DisposeAsync() => await _pw.DisposeAsync();

    // #user-name shows displayName when set, falling back to username otherwise (see showApp() in
    // index.html) - callers pass whichever one they expect to actually be displayed.
    private static async Task<IPage> LoginAsync(HostProcess host, IBrowser browser, string username, string password, string? expectedDisplayed = null)
    {
        var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync(host.BaseUrl);
        await page.ClickAsync("#signin-btn");
        await page.FillAsync("#login-username", username);
        await page.FillAsync("#login-password", password);
        await page.ClickAsync("#login-submit");
        await Expect(page.Locator("#user-name")).ToContainTextAsync(expectedDisplayed ?? username);
        return page;
    }

    [Fact]
    public async Task AdminUsersTab_ShowsLocalAccountsPanel_AndCreateRoundTrips()
    {
        await using var host = await HostProcess.StartLocalAsync(new[] { Admin }, initialAdmins: new[] { "admin" });
        var page = await LoginAsync(host, _pw.Browser, "admin", "admin123");

        await page.Locator("[data-nav=admin]").ClickAsync();
        await page.Locator("[data-testid=admin-tab-users]").ClickAsync();

        // The local-accounts panel (only rendered under mode=local) should be present alongside the
        // existing role-assignment card.
        await Expect(page.Locator("[data-testid=local-users-card]")).ToBeVisibleAsync();
        await Expect(page.Locator("[data-testid=local-user-create-form]")).ToBeVisibleAsync();

        await page.FillAsync("#lu-username", "newlocal");
        await page.FillAsync("#lu-displayname", "New Local");
        await page.FillAsync("#lu-password", "pw12345678");
        await page.Locator("[data-testid=create-local-user-btn]").ClickAsync();

        await Expect(page.Locator("[data-testid='local-user-row-newlocal']")).ToBeVisibleAsync();
        await Expect(page.Locator("[data-testid='local-user-row-newlocal']")).ToContainTextAsync("New Local");

        // The newly created local account can log in through the real form.
        var newUserPage = await LoginAsync(host, _pw.Browser, "newlocal", "pw12345678", expectedDisplayed: "New Local");
        await Expect(newUserPage.Locator("#user-role")).ToContainTextAsync("viewer");
    }

    // Under mode=ldap, the panel must not render at all (spec §9 "mode=ldap では非表示") - reuses the
    // existing Debug-only auth.localUsers harness, which defaults to mode=ldap.
    [Fact]
    public async Task AdminUsersTab_UnderLdapMode_DoesNotShowLocalAccountsPanel()
    {
        await using var host = await HostProcess.StartAsync(new[] { Admin }, initialAdmins: new[] { "admin" });
        var page = await LoginAsync(host, _pw.Browser, "admin", "admin123");

        await page.Locator("[data-nav=admin]").ClickAsync();
        await page.Locator("[data-testid=admin-tab-users]").ClickAsync();

        await Expect(page.Locator("[data-testid=local-users-card]")).ToHaveCountAsync(0);
    }
}
