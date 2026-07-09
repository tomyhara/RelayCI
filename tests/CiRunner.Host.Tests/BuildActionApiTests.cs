using System.Net;
using CiRunner.Host.Tests.Support;

namespace CiRunner.Host.Tests;

/// <summary>L3 tests for the build action HTTP surface: Abort and Rebuild (spec §5 F3/F5,
/// ci-runner-test-spec.md ENG-021, PRM-003). Driven through a real CiRunner.Host subprocess + real
/// powershell.exe builds, matching the pattern established for the F3a resource-lock tests.</summary>
public class BuildActionApiTests
{
    private static readonly TestLocalUser Admin = new("admin", "admin123", "admin");
    private static readonly TestLocalUser Viewer = new("vwr", "vwr123", "viewer");

    private static async Task<long> TriggerAsync(HttpClient client, string jobName)
    {
        var res = await client.PostAsync($"/api/jobs/{jobName}/trigger", null);
        res.EnsureSuccessStatusCode();
        using var body = await HttpJson.ReadJsonAsync(res);
        return body.RootElement.GetProperty("id").GetInt64();
    }

    private static async Task WaitForStatusAsync(HttpClient client, long buildId, string status, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        string? last = null;
        while (DateTime.UtcNow < deadline)
        {
            using var doc = await HttpJson.ReadJsonAsync(await client.GetAsync($"/api/builds/{buildId}"));
            last = doc.RootElement.GetProperty("status").GetString();
            if (last == status)
            {
                return;
            }
            await Task.Delay(100);
        }
        throw new TimeoutException($"build {buildId} did not reach status '{status}' in time (last seen: '{last}')");
    }

    // ENG-021: manual UI Abort kills the process tree and marks the build Aborted, through the same
    // mechanism the build-timeout path uses.
    [Fact]
    public async Task Abort_RunningBuild_MarksAborted_ENG021()
    {
        await using var host = await HostProcess.StartAsync(new[] { Admin }, initialAdmins: new[] { "admin" });
        await HttpJson.PostAsync(host.Client, "/api/login", new { username = "admin", password = "admin123" });
        await HttpJson.PostAsync(host.Client, "/api/admin/jobs", new { name = "abort-me" });
        File.WriteAllText(Path.Combine(host.Root, "jobs", "abort-me", "pipeline.cipipe"), "Stage \"Work\" { Start-Sleep -Seconds 30 }");

        var buildId = await TriggerAsync(host.Client, "abort-me");
        await WaitForStatusAsync(host.Client, buildId, "running", TimeSpan.FromSeconds(10));

        var abortRes = await host.Client.PostAsync($"/api/builds/{buildId}/abort", null);
        Assert.Equal(HttpStatusCode.OK, abortRes.StatusCode);

        await WaitForStatusAsync(host.Client, buildId, "aborted", TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Abort_UnknownBuild_Returns404()
    {
        await using var host = await HostProcess.StartAsync(new[] { Admin }, initialAdmins: new[] { "admin" });
        await HttpJson.PostAsync(host.Client, "/api/login", new { username = "admin", password = "admin123" });

        var res = await host.Client.PostAsync("/api/builds/999999/abort", null);
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Abort_AlreadyFinishedBuild_ReturnsBadRequest()
    {
        await using var host = await HostProcess.StartAsync(new[] { Admin }, initialAdmins: new[] { "admin" });
        await HttpJson.PostAsync(host.Client, "/api/login", new { username = "admin", password = "admin123" });
        await HttpJson.PostAsync(host.Client, "/api/admin/jobs", new { name = "quick" });
        File.WriteAllText(Path.Combine(host.Root, "jobs", "quick", "pipeline.cipipe"), "Stage \"Work\" { Write-Host \"done\" }");

        var buildId = await TriggerAsync(host.Client, "quick");
        await WaitForStatusAsync(host.Client, buildId, "success", TimeSpan.FromSeconds(15));

        var res = await host.Client.PostAsync($"/api/builds/{buildId}/abort", null);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Abort_ViewerRole_IsForbidden()
    {
        await using var host = await HostProcess.StartAsync(new[] { Admin, Viewer }, initialAdmins: new[] { "admin" });
        await HttpJson.PostAsync(host.Client, "/api/login", new { username = "vwr", password = "vwr123" });

        var res = await host.Client.PostAsync("/api/builds/1/abort", null);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    // PRM-003: Rebuild re-queues the same job tagged trigger=rebuild.
    [Fact]
    public async Task Rebuild_FinishedBuild_QueuesNewBuildTaggedRebuild_PRM003()
    {
        await using var host = await HostProcess.StartAsync(new[] { Admin }, initialAdmins: new[] { "admin" });
        await HttpJson.PostAsync(host.Client, "/api/login", new { username = "admin", password = "admin123" });
        await HttpJson.PostAsync(host.Client, "/api/admin/jobs", new { name = "rebuild-me" });
        File.WriteAllText(Path.Combine(host.Root, "jobs", "rebuild-me", "pipeline.cipipe"), "Stage \"Work\" { Write-Host \"done\" }");

        var originalId = await TriggerAsync(host.Client, "rebuild-me");
        await WaitForStatusAsync(host.Client, originalId, "success", TimeSpan.FromSeconds(15));

        var rebuildRes = await host.Client.PostAsync($"/api/builds/{originalId}/rebuild", null);
        Assert.Equal(HttpStatusCode.OK, rebuildRes.StatusCode);
        using var rebuilt = await HttpJson.ReadJsonAsync(rebuildRes);
        var newId = rebuilt.RootElement.GetProperty("id").GetInt64();
        Assert.NotEqual(originalId, newId);
        Assert.Equal("rebuild", rebuilt.RootElement.GetProperty("trigger").GetString());

        await WaitForStatusAsync(host.Client, newId, "success", TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task Rebuild_UnknownBuild_Returns404()
    {
        await using var host = await HostProcess.StartAsync(new[] { Admin }, initialAdmins: new[] { "admin" });
        await HttpJson.PostAsync(host.Client, "/api/login", new { username = "admin", password = "admin123" });

        var res = await host.Client.PostAsync("/api/builds/999999/rebuild", null);
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Rebuild_ViewerRole_IsForbidden()
    {
        await using var host = await HostProcess.StartAsync(new[] { Admin, Viewer }, initialAdmins: new[] { "admin" });
        await HttpJson.PostAsync(host.Client, "/api/login", new { username = "vwr", password = "vwr123" });

        var res = await host.Client.PostAsync("/api/builds/1/rebuild", null);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }
}
