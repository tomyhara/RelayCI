using System.Net;
using CiRunner.Host.Tests.Support;

namespace CiRunner.Host.Tests;

/// <summary>HTTP-level tests for auth.mode="local" (spec §9): local_users login, the admin
/// local-account management API, and the mode=ldap "these endpoints don't exist" requirement.
/// Mirrors AuthTests.cs's pattern of launching a real CiRunner.Host subprocess per test.</summary>
public class LocalAuthApiTests
{
    private static readonly TestLocalUser Admin = new("admin", "admin123", "admin");

    // Login success: session cookie issued, /api/me reflects displayName from local_users and a
    // null mail (spec §9: "mail / telephoneNumber は空. CI_USER_EMAIL は未設定となる").
    [Fact]
    public async Task Login_WithCorrectLocalCredentials_IssuesSessionAndExposesProfileWithNullMail()
    {
        await using var host = await HostProcess.StartLocalAsync(new[] { Admin }, initialAdmins: new[] { "admin" });

        var loginRes = await HttpJson.PostAsync(host.Client, "/api/login", new { username = "admin", password = "admin123" });
        Assert.Equal(HttpStatusCode.OK, loginRes.StatusCode);
        Assert.True(loginRes.Headers.Contains("Set-Cookie"));

        var meRes = await host.Client.GetAsync("/api/me");
        using var me = await HttpJson.ReadJsonAsync(meRes);
        Assert.True(me.RootElement.GetProperty("authenticated").GetBoolean());
        Assert.Equal("admin", me.RootElement.GetProperty("username").GetString());
        Assert.Equal("admin", me.RootElement.GetProperty("role").GetString());
        Assert.Equal("local", me.RootElement.GetProperty("authMode").GetString());
        Assert.True(me.RootElement.GetProperty("mail").ValueKind is System.Text.Json.JsonValueKind.Null);
    }

    // authMode is exposed even to an anonymous caller (spec §5 F6: "フロントがmodeを知る手段が必要"),
    // so the admin UI can decide whether to render the local-accounts panel before anyone logs in.
    [Fact]
    public async Task Me_ExposesAuthMode_EvenWhenNotAuthenticated()
    {
        await using var host = await HostProcess.StartLocalAsync(new[] { Admin }, initialAdmins: new[] { "admin" });

        var meRes = await host.Client.GetAsync("/api/me");
        using var me = await HttpJson.ReadJsonAsync(meRes);
        Assert.False(me.RootElement.GetProperty("authenticated").GetBoolean());
        Assert.Equal("local", me.RootElement.GetProperty("authMode").GetString());
    }

    [Fact]
    public async Task Login_WithWrongPassword_Returns401WithoutCookie()
    {
        await using var host = await HostProcess.StartLocalAsync(new[] { Admin }, initialAdmins: new[] { "admin" });

        var res = await HttpJson.PostAsync(host.Client, "/api/login", new { username = "admin", password = "wrong" });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.False(res.Headers.Contains("Set-Cookie"));
    }

    // Spec §9: local_users rows have an `enabled` flag; a disabled user must be rejected even with
    // the correct password.
    [Fact]
    public async Task Login_DisabledUser_IsRejectedEvenWithCorrectPassword()
    {
        await using var host = await HostProcess.StartLocalAsync(new[] { Admin }, initialAdmins: new[] { "admin" });
        HostProcess.SeedLocalUser(host.Root, "disableduser", "pw12345678", enabled: false);

        var res = await HttpJson.PostAsync(host.Client, "/api/login", new { username = "disableduser", password = "pw12345678" });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    // Spec §9: "同一ユーザー名について連続 5 回失敗で 30 秒のクールダウン" - the 6th attempt is
    // rejected even with the correct password. Not waiting out the real 30s here (that's covered by
    // a short-cooldown unit test in CiRunner.Core.Tests); this only proves the lockout engages.
    [Fact]
    public async Task Login_FiveConsecutiveFailures_LocksOutSixthAttemptEvenWithCorrectPassword()
    {
        await using var host = await HostProcess.StartLocalAsync(new[] { Admin }, initialAdmins: new[] { "admin" });
        HostProcess.SeedLocalUser(host.Root, "lockme", "correctpw1");

        for (var i = 0; i < 5; i++)
        {
            var fail = await HttpJson.PostAsync(host.Client, "/api/login", new { username = "lockme", password = "wrong" });
            Assert.Equal(HttpStatusCode.Unauthorized, fail.StatusCode);
        }

        var stillLocked = await HttpJson.PostAsync(host.Client, "/api/login", new { username = "lockme", password = "correctpw1" });
        Assert.Equal(HttpStatusCode.Unauthorized, stillLocked.StatusCode);
    }

    // Spec §9 password policy: minimum length only, default 8, enforced by the admin API too.
    [Fact]
    public async Task AdminApi_CreateLocalUser_RejectsPasswordShorterThanConfiguredMinimum()
    {
        await using var host = await HostProcess.StartLocalAsync(new[] { Admin }, initialAdmins: new[] { "admin" }, minPasswordLength: 10);
        await HttpJson.PostAsync(host.Client, "/api/login", new { username = "admin", password = "admin123" });

        var res = await HttpJson.PostAsync(host.Client, "/api/admin/local-users", new { username = "shortpw", password = "short1" });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);

        var listRes = await host.Client.GetAsync("/api/admin/local-users");
        using var list = await HttpJson.ReadJsonAsync(listRes);
        Assert.DoesNotContain(list.RootElement.EnumerateArray(), u => u.GetProperty("username").GetString() == "shortpw");
    }

    // Full lifecycle through the admin API: create -> appears in list -> password reset -> disable
    // -> login rejected -> delete -> gone. Each step is audit logged (spec §5 F6 common rule).
    [Fact]
    public async Task AdminApi_LocalUserLifecycle_IsAuditLogged()
    {
        await using var host = await HostProcess.StartLocalAsync(new[] { Admin }, initialAdmins: new[] { "admin" });
        await HttpJson.PostAsync(host.Client, "/api/login", new { username = "admin", password = "admin123" });

        var createRes = await HttpJson.PostAsync(host.Client, "/api/admin/local-users", new { username = "kenji", password = "pw12345678", displayName = "Kenji" });
        Assert.Equal(HttpStatusCode.OK, createRes.StatusCode);

        var listRes = await host.Client.GetAsync("/api/admin/local-users");
        using var list = await HttpJson.ReadJsonAsync(listRes);
        Assert.Contains(list.RootElement.EnumerateArray(), u => u.GetProperty("username").GetString() == "kenji");

        // Newly created user can log in with the password just set.
        using (var freshClient = new HttpClient { BaseAddress = host.Client.BaseAddress })
        {
            var kenjiLogin = await HttpJson.PostAsync(freshClient, "/api/login", new { username = "kenji", password = "pw12345678" });
            Assert.Equal(HttpStatusCode.OK, kenjiLogin.StatusCode);
        }

        var passwdRes = await HttpJson.PostAsync(host.Client, "/api/admin/local-users/kenji/password", new { password = "newpassword2" });
        Assert.Equal(HttpStatusCode.OK, passwdRes.StatusCode);

        var disableRes = await HttpJson.PostAsync(host.Client, "/api/admin/local-users/kenji/enabled", new { enabled = false });
        Assert.Equal(HttpStatusCode.OK, disableRes.StatusCode);

        using (var freshClient = new HttpClient { BaseAddress = host.Client.BaseAddress })
        {
            var disabledLogin = await HttpJson.PostAsync(freshClient, "/api/login", new { username = "kenji", password = "newpassword2" });
            Assert.Equal(HttpStatusCode.Unauthorized, disabledLogin.StatusCode);
        }

        var deleteRes = await host.Client.DeleteAsync("/api/admin/local-users/kenji");
        Assert.Equal(HttpStatusCode.OK, deleteRes.StatusCode);

        var listAfterDelete = await host.Client.GetAsync("/api/admin/local-users");
        using var listAfter = await HttpJson.ReadJsonAsync(listAfterDelete);
        Assert.DoesNotContain(listAfter.RootElement.EnumerateArray(), u => u.GetProperty("username").GetString() == "kenji");

        var auditRes = await host.Client.GetAsync("/api/audit");
        using var audit = await HttpJson.ReadJsonAsync(auditRes);
        var actions = audit.RootElement.EnumerateArray().Select(e => e.GetProperty("action").GetString()).ToList();
        Assert.Contains("localuser.create", actions);
        Assert.Contains("localuser.passwd", actions);
        Assert.Contains("localuser.disable", actions);
        Assert.Contains("localuser.delete", actions);
    }

    [Fact]
    public async Task AdminApi_CreateLocalUser_RejectsDuplicateUsername()
    {
        await using var host = await HostProcess.StartLocalAsync(new[] { Admin }, initialAdmins: new[] { "admin" });
        await HttpJson.PostAsync(host.Client, "/api/login", new { username = "admin", password = "admin123" });

        var first = await HttpJson.PostAsync(host.Client, "/api/admin/local-users", new { username = "dupe", password = "pw12345678" });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await HttpJson.PostAsync(host.Client, "/api/admin/local-users", new { username = "dupe", password = "pw12345678" });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task AdminApi_RequiresAdminRole()
    {
        await using var host = await HostProcess.StartLocalAsync(new[] { Admin, new TestLocalUser("vwr", "vwr123", "viewer") }, initialAdmins: new[] { "admin" });
        await HttpJson.PostAsync(host.Client, "/api/login", new { username = "vwr", password = "vwr123" });

        var res = await host.Client.GetAsync("/api/admin/local-users");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    // Spec §9/test-spec requirement: under mode=ldap, the local-account admin API must not merely be
    // forbidden but genuinely unmapped (404) - these routes are only ever registered for mode=local.
    // Reuses the existing Debug-only auth.localUsers harness (mode defaults to "ldap").
    [Fact]
    public async Task UnderLdapMode_LocalUserAdminApi_Is404NotJustForbidden()
    {
        await using var host = await HostProcess.StartAsync(new[] { Admin }, initialAdmins: new[] { "admin" });
        await HttpJson.PostAsync(host.Client, "/api/login", new { username = "admin", password = "admin123" });

        var listRes = await host.Client.GetAsync("/api/admin/local-users");
        Assert.Equal(HttpStatusCode.NotFound, listRes.StatusCode);

        var createRes = await HttpJson.PostAsync(host.Client, "/api/admin/local-users", new { username = "x", password = "pw12345678" });
        Assert.Equal(HttpStatusCode.NotFound, createRes.StatusCode);
    }
}
