using System.Net;
using CiRunner.Host.Tests.Support;

namespace CiRunner.Host.Tests;

/// <summary>L3 auth tests (ci-runner-test-spec.md §3.5, AUTH-001..009/011). Each test launches a real
/// CiRunner.Host subprocess against auth.localUsers (the Debug-only LDAP test double) on a random port.</summary>
public class AuthTests
{
    private static readonly TestLocalUser Admin = new("admin", "admin123", "admin");
    private static readonly TestLocalUser Viewer = new("vwr", "vwr123", "viewer");
    private static readonly TestLocalUser Operator = new("opr", "opr123", "operator");

    // AUTH-001: correct credentials issue a session cookie and /api/me reflects the profile.
    [Fact]
    public async Task Login_WithCorrectCredentials_IssuesSessionAndExposesProfile()
    {
        await using var host = await HostProcess.StartAsync(new[] { Admin }, initialAdmins: new[] { "admin" });

        var loginRes = await HttpJson.PostAsync(host.Client, "/api/login", new { username = "admin", password = "admin123" });
        Assert.Equal(HttpStatusCode.OK, loginRes.StatusCode);
        Assert.True(loginRes.Headers.Contains("Set-Cookie"));

        var meRes = await host.Client.GetAsync("/api/me");
        using var me = await HttpJson.ReadJsonAsync(meRes);
        Assert.True(me.RootElement.GetProperty("authenticated").GetBoolean());
        Assert.Equal("admin", me.RootElement.GetProperty("username").GetString());
        Assert.Equal("admin", me.RootElement.GetProperty("role").GetString());
        Assert.Equal("admin@example.test", me.RootElement.GetProperty("mail").GetString());
    }

    // AUTH-002: wrong password / unknown user -> 401, no cookie, still locked out of protected APIs.
    [Fact]
    public async Task Login_WithWrongPasswordOrUnknownUser_Returns401WithoutCookie()
    {
        await using var host = await HostProcess.StartAsync(new[] { Admin }, initialAdmins: new[] { "admin" });

        var wrongPassword = await HttpJson.PostAsync(host.Client, "/api/login", new { username = "admin", password = "wrong" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        Assert.False(wrongPassword.Headers.Contains("Set-Cookie"));

        var unknownUser = await HttpJson.PostAsync(host.Client, "/api/login", new { username = "ghost", password = "x" });
        Assert.Equal(HttpStatusCode.Unauthorized, unknownUser.StatusCode);

        var jobsRes = await host.Client.GetAsync("/api/jobs");
        Assert.Equal(HttpStatusCode.Unauthorized, jobsRes.StatusCode);
    }

    // AUTH-004: no explicit role -> falls back to defaultRole; defaultRole=deny -> 403 at login.
    [Fact]
    public async Task UnassignedUser_FallsBackToDefaultRole()
    {
        var nobody = new TestLocalUser("nobody", "nobody123", "viewer");
        await using var host = await HostProcess.StartAsync(new[] { Admin, nobody }, initialAdmins: new[] { "admin" }, defaultRole: "viewer");

        var loginRes = await HttpJson.PostAsync(host.Client, "/api/login", new { username = "nobody", password = "nobody123" });
        Assert.Equal(HttpStatusCode.OK, loginRes.StatusCode);

        var meRes = await host.Client.GetAsync("/api/me");
        using var me = await HttpJson.ReadJsonAsync(meRes);
        Assert.Equal("viewer", me.RootElement.GetProperty("role").GetString());
    }

    [Fact]
    public async Task UnassignedUser_WithDefaultRoleDeny_IsForbiddenAtLogin()
    {
        var nobody = new TestLocalUser("nobody", "nobody123", "viewer");
        await using var host = await HostProcess.StartAsync(new[] { Admin, nobody }, initialAdmins: new[] { "admin" }, defaultRole: "deny");

        var loginRes = await HttpJson.PostAsync(host.Client, "/api/login", new { username = "nobody", password = "nobody123" });
        Assert.Equal(HttpStatusCode.Forbidden, loginRes.StatusCode);
    }

    // AUTH-005: initialAdmins is only applied while user_roles is empty; a second boot against the
    // same DB must not silently re-grant admin after it was manually revoked.
    [Fact]
    public async Task InitialAdmins_OnlyAppliesOnFirstBootWithEmptyUserRoles()
    {
        var second = new TestLocalUser("second", "second123", "admin");
        var users = new[] { Admin, second };
        var root = Path.Combine(Path.GetTempPath(), $"ci-host-{Guid.NewGuid()}");
        var port = HostProcess.GetFreePort();
        var configPath = HostProcess.WriteConfig(root, port, users, initialAdmins: new[] { "admin", "second" });

        try
        {
            var host1 = await HostProcess.StartAsync(root, port, configPath, deleteRootOnDispose: false);
            var adminClient = host1.Client;
            await HttpJson.PostAsync(adminClient, "/api/login", new { username = "admin", password = "admin123" });

            // Both initial admins should have been bootstrapped as admin.
            var usersRes1 = await adminClient.GetAsync("/api/users");
            using var users1 = await HttpJson.ReadJsonAsync(usersRes1);
            Assert.Contains(users1.RootElement.EnumerateArray(), u => u.GetProperty("username").GetString() == "second" && u.GetProperty("role").GetString() == "admin");

            // Demote "second" down to viewer (allowed: "admin" is still a second admin).
            var demoteRes = await HttpJson.PostAsync(adminClient, "/api/users/second/role", new { role = "viewer" });
            Assert.Equal(HttpStatusCode.OK, demoteRes.StatusCode);

            await host1.StopAsync();
            await host1.DisposeAsync();

            await using var host2 = await HostProcess.StartAsync(root, port, configPath, deleteRootOnDispose: true);
            var adminClient2 = host2.Client;
            await HttpJson.PostAsync(adminClient2, "/api/login", new { username = "admin", password = "admin123" });
            var usersRes2 = await adminClient2.GetAsync("/api/users");
            using var users2 = await HttpJson.ReadJsonAsync(usersRes2);
            var secondEntry = users2.RootElement.EnumerateArray().First(u => u.GetProperty("username").GetString() == "second");
            Assert.Equal("viewer", secondEntry.GetProperty("role").GetString());
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    // AUTH-006: the API refuses to demote or remove the last remaining admin.
    [Fact]
    public async Task CannotDemoteOrRemoveTheLastAdmin()
    {
        await using var host = await HostProcess.StartAsync(new[] { Admin }, initialAdmins: new[] { "admin" });
        await HttpJson.PostAsync(host.Client, "/api/login", new { username = "admin", password = "admin123" });

        var demote = await HttpJson.PostAsync(host.Client, "/api/users/admin/role", new { role = "viewer" });
        Assert.Equal(HttpStatusCode.BadRequest, demote.StatusCode);

        var remove = await host.Client.DeleteAsync("/api/users/admin/role");
        Assert.Equal(HttpStatusCode.BadRequest, remove.StatusCode);
    }

    // AUTH-007 (subset): role x endpoint matrix - viewer is blocked from operator/admin actions,
    // operator is blocked from admin-only actions, admin can reach everything.
    [Fact]
    public async Task RoleEndpointMatrix_EnforcesMinimumRole()
    {
        await using var host = await HostProcess.StartAsync(new[] { Admin, Viewer, Operator }, initialAdmins: new[] { "admin" });

        await HttpJson.PostAsync(host.Client, "/api/login", new { username = "admin", password = "admin123" });
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/api/jobs")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/api/users")).StatusCode);
        // auth.localUsers only supplies username/password (spec §9: role is always DB-driven via
        // user_roles, never trusted from the identity provider) - assign "opr" explicitly.
        await HttpJson.PostAsync(host.Client, "/api/users/opr/role", new { role = "operator" });
        await host.Client.PostAsync("/api/logout", null);

        await HttpJson.PostAsync(host.Client, "/api/login", new { username = "vwr", password = "vwr123" });
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/api/jobs")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.Client.PostAsync("/api/jobs/nope/trigger", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.Client.GetAsync("/api/users")).StatusCode);
        await host.Client.PostAsync("/api/logout", null);

        await HttpJson.PostAsync(host.Client, "/api/login", new { username = "opr", password = "opr123" });
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.PostAsync("/api/jobs/nope/trigger", null)).StatusCode); // operator allowed through to the handler
        Assert.Equal(HttpStatusCode.Forbidden, (await host.Client.GetAsync("/api/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.Client.PostAsync("/api/tokens", null)).StatusCode);
    }

    // AUTH-008: the webhook receiver is reachable without a session (it authenticates via HMAC, not
    // cookies/tokens) - a fresh unauthenticated client must not get a 401 from the auth middleware.
    [Fact]
    public async Task WebhookEndpoint_IsReachableWithoutAuthentication()
    {
        await using var host = await HostProcess.StartAsync(new[] { Admin }, initialAdmins: new[] { "admin" });
        var res = await host.Client.PostAsync("/api/webhook/no-such-hook", new StringContent("{}"));
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode); // hook-not-found, not 401
    }

    // AUTH-009 + AUTH-011 (token portion): issue -> Bearer access -> revoke -> 401, with audit_log entries.
    [Fact]
    public async Task ApiToken_IssueUseRevokeLifecycle_IsAuditLogged()
    {
        await using var host = await HostProcess.StartAsync(new[] { Admin }, initialAdmins: new[] { "admin" });
        await HttpJson.PostAsync(host.Client, "/api/login", new { username = "admin", password = "admin123" });

        var issueRes = await HttpJson.PostAsync(host.Client, "/api/tokens", new { name = "ci-test", role = "operator" });
        Assert.Equal(HttpStatusCode.OK, issueRes.StatusCode);
        using var issued = await HttpJson.ReadJsonAsync(issueRes);
        var rawToken = issued.RootElement.GetProperty("token").GetString();
        var tokenId = issued.RootElement.GetProperty("id").GetInt64();

        using var bearerClient = new HttpClient { BaseAddress = host.Client.BaseAddress };
        bearerClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", rawToken);
        Assert.Equal(HttpStatusCode.OK, (await bearerClient.GetAsync("/api/jobs")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bearerClient.GetAsync("/api/users")).StatusCode); // token role is operator, not admin

        var revokeRes = await host.Client.DeleteAsync($"/api/tokens/{tokenId}");
        Assert.Equal(HttpStatusCode.OK, revokeRes.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await bearerClient.GetAsync("/api/jobs")).StatusCode);

        var auditRes = await host.Client.GetAsync("/api/audit");
        using var audit = await HttpJson.ReadJsonAsync(auditRes);
        var actions = audit.RootElement.EnumerateArray().Select(e => e.GetProperty("action").GetString()).ToList();
        Assert.Contains("token.issue", actions);
        Assert.Contains("token.revoke", actions);
    }

    // AUTH-011 (role portion): role assignment/removal is audit logged with before/after.
    [Fact]
    public async Task RoleAssignment_IsAuditLoggedWithBeforeAfter()
    {
        await using var host = await HostProcess.StartAsync(new[] { Admin, Viewer }, initialAdmins: new[] { "admin" });
        await HttpJson.PostAsync(host.Client, "/api/login", new { username = "admin", password = "admin123" });

        await HttpJson.PostAsync(host.Client, "/api/users/vwr/role", new { role = "operator" });

        var auditRes = await host.Client.GetAsync("/api/audit");
        using var audit = await HttpJson.ReadJsonAsync(auditRes);
        var entry = audit.RootElement.EnumerateArray().First(e => e.GetProperty("action").GetString() == "role.assign" && e.GetProperty("target").GetString() == "vwr");
        Assert.Contains("operator", entry.GetProperty("afterJson").GetString());
    }
}
