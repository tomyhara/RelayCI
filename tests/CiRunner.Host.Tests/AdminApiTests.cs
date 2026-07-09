using System.Net;
using CiRunner.Host.Tests.Support;

namespace CiRunner.Host.Tests;

/// <summary>L3 tests for the F6 admin API (spec §5 F6): settings, job/hook CRUD with on-disk
/// persistence, resource descriptions, and role gating. Each test launches a real CiRunner.Host
/// subprocess, matching the pattern established for the §9 auth tests.</summary>
public class AdminApiTests
{
    private static readonly TestLocalUser Admin = new("admin", "admin123", "admin");
    private static readonly TestLocalUser Viewer = new("vwr", "vwr123", "viewer");

    private static async Task<HttpClient> LoggedInAdminAsync(HostProcess host)
    {
        await HttpJson.PostAsync(host.Client, "/api/login", new { username = "admin", password = "admin123" });
        return host.Client;
    }

    [Fact]
    public async Task Settings_GetReturnsDefaults_PostUpdatesAndValidates()
    {
        await using var host = await HostProcess.StartAsync(new[] { Admin }, initialAdmins: new[] { "admin" });
        var client = await LoggedInAdminAsync(host);

        var getRes = await client.GetAsync("/api/settings");
        using var initial = await HttpJson.ReadJsonAsync(getRes);
        Assert.Equal(2, initial.RootElement.GetProperty("executors").GetInt32());

        var badRes = await HttpJson.PostAsync(client, "/api/settings", new { executors = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, badRes.StatusCode);

        var okRes = await HttpJson.PostAsync(client, "/api/settings", new { executors = 5, testResultMode = "exit-code-only" });
        Assert.Equal(HttpStatusCode.OK, okRes.StatusCode);

        using var after = await HttpJson.ReadJsonAsync(await client.GetAsync("/api/settings"));
        Assert.Equal(5, after.RootElement.GetProperty("executors").GetInt32());
        Assert.Equal("exit-code-only", after.RootElement.GetProperty("testResultMode").GetString());
    }

    [Fact]
    public async Task Jobs_CreateUpdateDelete_PersistsToDiskAndDatabase()
    {
        await using var host = await HostProcess.StartAsync(new[] { Admin }, initialAdmins: new[] { "admin" });
        var client = await LoggedInAdminAsync(host);

        var createRes = await HttpJson.PostAsync(client, "/api/admin/jobs", new
        {
            name = "demo-job",
            resources = new[] { "bench-1" },
            cronSchedules = new[] { "*/5 * * * *" },
            parameters = new[] { new { Name = "config", Default = "Release", Description = "", Required = false } },
        });
        Assert.Equal(HttpStatusCode.OK, createRes.StatusCode);

        var jobJsonPath = Path.Combine(host.Root, "jobs", "demo-job", "job.json");
        var pipelinePath = Path.Combine(host.Root, "jobs", "demo-job", "pipeline.cipipe");
        Assert.True(File.Exists(jobJsonPath), "job.json should be written to disk so a restart's JobScanner reproduces it");
        Assert.True(File.Exists(pipelinePath), "a pipeline.cipipe template should be created for a new server-mode job");
        Assert.Contains("bench-1", File.ReadAllText(jobJsonPath));

        var dupRes = await HttpJson.PostAsync(client, "/api/admin/jobs", new { name = "demo-job" });
        Assert.Equal(HttpStatusCode.Conflict, dupRes.StatusCode);

        var invalidNameRes = await HttpJson.PostAsync(client, "/api/admin/jobs", new { name = "../escape" });
        Assert.Equal(HttpStatusCode.BadRequest, invalidNameRes.StatusCode);

        var repoWithoutUrlRes = await HttpJson.PostAsync(client, "/api/admin/jobs", new { name = "repo-job", pipelineSource = "repo" });
        Assert.Equal(HttpStatusCode.BadRequest, repoWithoutUrlRes.StatusCode);

        var putReq = new HttpRequestMessage(HttpMethod.Put, "/api/admin/jobs/demo-job")
        {
            Content = new StringContent("{\"enabled\":false}", System.Text.Encoding.UTF8, "application/json"),
        };
        var putRes = await client.SendAsync(putReq);
        Assert.Equal(HttpStatusCode.OK, putRes.StatusCode);
        using var updated = await HttpJson.ReadJsonAsync(putRes);
        Assert.False(updated.RootElement.GetProperty("enabled").GetBoolean());

        var deleteRes = await client.DeleteAsync("/api/admin/jobs/demo-job");
        Assert.Equal(HttpStatusCode.OK, deleteRes.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/admin/jobs/demo-job")).StatusCode);
    }

    [Fact]
    public async Task Jobs_RecreateAfterDelete_ReactivatesRatherThanCrashing()
    {
        await using var host = await HostProcess.StartAsync(new[] { Admin }, initialAdmins: new[] { "admin" });
        var client = await LoggedInAdminAsync(host);

        await HttpJson.PostAsync(client, "/api/admin/jobs", new { name = "reused-name" });
        await client.DeleteAsync("/api/admin/jobs/reused-name");

        var recreateRes = await HttpJson.PostAsync(client, "/api/admin/jobs", new { name = "reused-name" });
        Assert.Equal(HttpStatusCode.OK, recreateRes.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/admin/jobs/reused-name")).StatusCode);
    }

    [Fact]
    public async Task Hooks_RecreateAfterDelete_ReactivatesRatherThanCrashing()
    {
        await using var host = await HostProcess.StartAsync(new[] { Admin }, initialAdmins: new[] { "admin" });
        var client = await LoggedInAdminAsync(host);

        await HttpJson.PostAsync(client, "/api/admin/hooks", new { name = "reused-hook" });
        await client.DeleteAsync("/api/admin/hooks/reused-hook");

        var recreateRes = await HttpJson.PostAsync(client, "/api/admin/hooks", new { name = "reused-hook" });
        Assert.Equal(HttpStatusCode.OK, recreateRes.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/admin/hooks/reused-hook")).StatusCode);
    }

    [Fact]
    public async Task Jobs_ExportThenImport_RecreatesEquivalentJob()
    {
        await using var host = await HostProcess.StartAsync(new[] { Admin }, initialAdmins: new[] { "admin" });
        var client = await LoggedInAdminAsync(host);

        await HttpJson.PostAsync(client, "/api/admin/jobs", new { name = "export-me", resources = new[] { "bench-9" } });
        var exportRes = await client.GetAsync("/api/admin/jobs/export-me/export");
        var exportJson = await exportRes.Content.ReadAsStringAsync();

        await client.DeleteAsync("/api/admin/jobs/export-me");
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/admin/jobs/export-me")).StatusCode);

        var importReq = new HttpRequestMessage(HttpMethod.Post, "/api/admin/jobs/import")
        {
            Content = new StringContent(exportJson, System.Text.Encoding.UTF8, "application/json"),
        };
        var importRes = await client.SendAsync(importReq);
        Assert.Equal(HttpStatusCode.OK, importRes.StatusCode);

        using var reimported = await HttpJson.ReadJsonAsync(await client.GetAsync("/api/admin/jobs/export-me"));
        Assert.Contains("bench-9", reimported.RootElement.GetProperty("resources").GetString());
    }

    [Fact]
    public async Task Hooks_CreateUpdateDelete_MasksSecretAndPersistsToDisk()
    {
        await using var host = await HostProcess.StartAsync(new[] { Admin }, initialAdmins: new[] { "admin" });
        var client = await LoggedInAdminAsync(host);

        var createRes = await HttpJson.PostAsync(client, "/api/admin/hooks", new { name = "gh-demo", secret = "s3cr3t", timeoutSec = 30 });
        Assert.Equal(HttpStatusCode.OK, createRes.StatusCode);
        using var created = await HttpJson.ReadJsonAsync(createRes);
        Assert.True(created.RootElement.GetProperty("hasSecret").GetBoolean());
        Assert.False(created.RootElement.TryGetProperty("secret", out _), "the raw secret must never be echoed back");

        var handlerPath = Path.Combine(host.Root, "hooks", "gh-demo.cipipe");
        var configPath = Path.Combine(host.Root, "hooks", "gh-demo.json");
        Assert.True(File.Exists(handlerPath));
        Assert.True(File.Exists(configPath));

        var listRes = await client.GetAsync("/api/admin/hooks");
        using var list = await HttpJson.ReadJsonAsync(listRes);
        Assert.All(list.RootElement.EnumerateArray(), h => Assert.False(h.TryGetProperty("secret", out _)));

        var runsRes = await client.GetAsync("/api/admin/hooks/gh-demo/runs");
        Assert.Equal(HttpStatusCode.OK, runsRes.StatusCode);

        var deleteRes = await client.DeleteAsync("/api/admin/hooks/gh-demo");
        Assert.Equal(HttpStatusCode.OK, deleteRes.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/admin/hooks/gh-demo")).StatusCode);
    }

    [Fact]
    public async Task Resources_CreateListDelete()
    {
        await using var host = await HostProcess.StartAsync(new[] { Admin }, initialAdmins: new[] { "admin" });
        var client = await LoggedInAdminAsync(host);

        await HttpJson.PostAsync(client, "/api/admin/resources", new { name = "bench-1", description = "HIL bench #1" });
        using var list = await HttpJson.ReadJsonAsync(await client.GetAsync("/api/admin/resources"));
        Assert.Single(list.RootElement.EnumerateArray());

        var deleteRes = await client.DeleteAsync("/api/admin/resources/bench-1");
        Assert.Equal(HttpStatusCode.OK, deleteRes.StatusCode);
        using var afterDelete = await HttpJson.ReadJsonAsync(await client.GetAsync("/api/admin/resources"));
        Assert.Empty(afterDelete.RootElement.EnumerateArray());
    }

    [Fact]
    public async Task AdminEndpoints_RejectNonAdminRoles()
    {
        await using var host = await HostProcess.StartAsync(new[] { Admin, Viewer }, initialAdmins: new[] { "admin" });
        await HttpJson.PostAsync(host.Client, "/api/login", new { username = "vwr", password = "vwr123" });

        Assert.Equal(HttpStatusCode.Forbidden, (await host.Client.GetAsync("/api/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.Client.GetAsync("/api/admin/jobs")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.Client.GetAsync("/api/admin/hooks")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.Client.GetAsync("/api/admin/resources")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await HttpJson.PostAsync(host.Client, "/api/admin/jobs", new { name = "x" })).StatusCode);
    }
}
