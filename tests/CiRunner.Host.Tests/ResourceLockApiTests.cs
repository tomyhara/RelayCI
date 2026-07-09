using System.Net;
using CiRunner.Host.Tests.Support;

namespace CiRunner.Host.Tests;

/// <summary>L3 tests for the F3a resource-lock HTTP surface (spec §5 F3a, ci-runner-test-spec.md
/// ENG-010/014): the /api/queue "blocked by" projection and the admin force-release endpoint,
/// driven through the real subprocess + real powershell.exe builds.</summary>
public class ResourceLockApiTests
{
    private static readonly TestLocalUser Admin = new("admin", "admin123", "admin");

    private static async Task CreateJobWithResourceAsync(HttpClient client, string name, string resource)
    {
        var createRes = await HttpJson.PostAsync(client, "/api/admin/jobs", new { name, resources = new[] { resource } });
        createRes.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Queue_ReportsBlockingBuildForAWaitingBuild_ENG010()
    {
        await using var host = await HostProcess.StartAsync(new[] { Admin }, initialAdmins: new[] { "admin" });
        await HttpJson.PostAsync(host.Client, "/api/login", new { username = "admin", password = "admin123" });

        await CreateJobWithResourceAsync(host.Client, "res-a", "bench-1");
        await CreateJobWithResourceAsync(host.Client, "res-b", "bench-1");
        File.WriteAllText(Path.Combine(host.Root, "jobs", "res-a", "pipeline.cipipe"), "Stage \"Work\" { Start-Sleep -Milliseconds 2500 }");
        File.WriteAllText(Path.Combine(host.Root, "jobs", "res-b", "pipeline.cipipe"), "Stage \"Work\" { Start-Sleep -Milliseconds 200 }");

        await host.Client.PostAsync("/api/jobs/res-a/trigger", null);
        await Task.Delay(500);
        await host.Client.PostAsync("/api/jobs/res-b/trigger", null);
        await Task.Delay(500);

        using var queue = await HttpJson.ReadJsonAsync(await host.Client.GetAsync("/api/queue"));
        var waiting = queue.RootElement.EnumerateArray().Single(b => b.GetProperty("jobName").GetString() == "res-b");
        Assert.Equal("waiting", waiting.GetProperty("status").GetString());
        var blockedBy = waiting.GetProperty("blockedBy").EnumerateArray().Single();
        Assert.Equal("bench-1", blockedBy.GetProperty("resource").GetString());
        Assert.Equal("res-a", blockedBy.GetProperty("jobName").GetString());

        using var resources = await HttpJson.ReadJsonAsync(await host.Client.GetAsync("/api/admin/resources"));
        var benchOne = resources.RootElement.EnumerateArray().Single(r => r.GetProperty("name").GetString() == "bench-1");
        Assert.Equal("res-a", benchOne.GetProperty("heldByJobName").GetString());
        Assert.Equal(1, benchOne.GetProperty("waitingCount").GetInt32());
    }

    // ENG-014: admin force-release frees the resource, lets the waiting build proceed, and is audit logged.
    [Fact]
    public async Task ForceRelease_FreesResourceAndUnblocksWaitingBuild_ENG014()
    {
        await using var host = await HostProcess.StartAsync(new[] { Admin }, initialAdmins: new[] { "admin" });
        await HttpJson.PostAsync(host.Client, "/api/login", new { username = "admin", password = "admin123" });

        await CreateJobWithResourceAsync(host.Client, "res-a", "bench-1");
        await CreateJobWithResourceAsync(host.Client, "res-b", "bench-1");
        // res-a "hangs" far longer than the test would otherwise wait, standing in for a stuck build.
        File.WriteAllText(Path.Combine(host.Root, "jobs", "res-a", "pipeline.cipipe"), "Stage \"Work\" { Start-Sleep -Milliseconds 60000 }");
        File.WriteAllText(Path.Combine(host.Root, "jobs", "res-b", "pipeline.cipipe"), "Stage \"Work\" { Start-Sleep -Milliseconds 200 }");

        await host.Client.PostAsync("/api/jobs/res-a/trigger", null);
        await Task.Delay(500);
        await host.Client.PostAsync("/api/jobs/res-b/trigger", null);
        await Task.Delay(500);

        using var beforeQueue = await HttpJson.ReadJsonAsync(await host.Client.GetAsync("/api/queue"));
        Assert.Contains(beforeQueue.RootElement.EnumerateArray(), b => b.GetProperty("jobName").GetString() == "res-b" && b.GetProperty("status").GetString() == "waiting");

        var releaseRes = await host.Client.PostAsync("/api/admin/resources/bench-1/release", null);
        Assert.Equal(HttpStatusCode.OK, releaseRes.StatusCode);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        string? resBStatus = null;
        while (DateTime.UtcNow < deadline)
        {
            using var builds = await HttpJson.ReadJsonAsync(await host.Client.GetAsync("/api/jobs/res-b/builds"));
            resBStatus = builds.RootElement.EnumerateArray().FirstOrDefault().GetProperty("status").GetString();
            if (resBStatus is "success" or "running") break;
            await Task.Delay(200);
        }
        Assert.True(resBStatus is "success" or "running", $"expected res-b to proceed after force-release, was '{resBStatus}'");

        using var audit = await HttpJson.ReadJsonAsync(await host.Client.GetAsync("/api/audit"));
        var entry = audit.RootElement.EnumerateArray().First(e => e.GetProperty("action").GetString() == "resource.force_release");
        Assert.Equal("bench-1", entry.GetProperty("target").GetString());
    }

    [Fact]
    public async Task ForceRelease_UnheldResource_Returns404()
    {
        await using var host = await HostProcess.StartAsync(new[] { Admin }, initialAdmins: new[] { "admin" });
        await HttpJson.PostAsync(host.Client, "/api/login", new { username = "admin", password = "admin123" });

        var res = await host.Client.PostAsync("/api/admin/resources/never-held/release", null);
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }
}
