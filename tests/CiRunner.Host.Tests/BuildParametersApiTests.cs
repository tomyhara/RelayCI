using System.Text.Json;
using CiRunner.Host.Tests.Support;

namespace CiRunner.Host.Tests;

/// <summary>L3 tests that the API surfaces build parameters both ways (spec §5 F1a): GET /api/jobs
/// exposes each job's parameter definitions (so the UI can render a trigger form), and GET
/// /api/builds/{id} returns the {name:value} parameters a build actually ran with ("使用したパラメータ
/// は builds.parameters に JSON 保存し、ビルド詳細に表示"). Driven through a real CiRunner.Host subprocess
/// + real powershell.exe builds, matching BuildActionApiTests.</summary>
public class BuildParametersApiTests
{
    private static readonly TestLocalUser Admin = new("admin", "admin123", "admin");

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

    // /api/jobs lists parameter definitions; /api/builds/{id} returns the resolved values used, with the
    // undeclared/required rules applied by ParameterResolver (Target falls back to its default here).
    [Fact]
    public async Task BuildDetail_IncludesUsedParameters_AndJobsListsDefinitions_F1a()
    {
        await using var host = await HostProcess.StartAsync(new[] { Admin }, initialAdmins: new[] { "admin" });
        await HttpJson.PostAsync(host.Client, "/api/login", new { username = "admin", password = "admin123" });
        await HttpJson.PostAsync(host.Client, "/api/admin/jobs", new
        {
            name = "params-job",
            parameters = new[]
            {
                new { Name = "Target", Default = (string?)"prod", Description = "deploy target", Required = false },
                new { Name = "Ref", Default = (string?)null, Description = "git ref", Required = true },
            },
        });
        File.WriteAllText(Path.Combine(host.Root, "jobs", "params-job", "pipeline.cipipe"),
            "Stage \"Echo\" { Write-Host \"T=$env:Target R=$env:Ref\" }");

        // GET /api/jobs exposes the parameter definitions the UI uses to decide whether to prompt.
        using (var jobsDoc = await HttpJson.ReadJsonAsync(await host.Client.GetAsync("/api/jobs")))
        {
            var job = jobsDoc.RootElement.EnumerateArray().First(j => j.GetProperty("name").GetString() == "params-job");
            var defs = job.GetProperty("parameters");
            Assert.Equal(2, defs.GetArrayLength());
            Assert.Equal("Target", defs[0].GetProperty("name").GetString());
            Assert.Equal("prod", defs[0].GetProperty("default").GetString());
            Assert.Equal("Ref", defs[1].GetProperty("name").GetString());
            Assert.True(defs[1].GetProperty("required").GetBoolean());
        }

        // Trigger with an explicit value for the required 'Ref'; 'Target' falls back to its default.
        var triggerRes = await HttpJson.PostAsync(host.Client, "/api/jobs/params-job/trigger",
            new { parameters = new { Ref = "release/1.2" } });
        triggerRes.EnsureSuccessStatusCode();
        using var triggered = await HttpJson.ReadJsonAsync(triggerRes);
        var buildId = triggered.RootElement.GetProperty("id").GetInt64();

        await WaitForStatusAsync(host.Client, buildId, "success", TimeSpan.FromSeconds(20));

        // GET /api/builds/{id} returns the parameters the build actually ran with, as a {name:value} object.
        using var buildDoc = await HttpJson.ReadJsonAsync(await host.Client.GetAsync($"/api/builds/{buildId}"));
        var used = buildDoc.RootElement.GetProperty("parameters");
        Assert.Equal(JsonValueKind.Object, used.ValueKind);
        Assert.Equal("release/1.2", used.GetProperty("Ref").GetString());
        Assert.Equal("prod", used.GetProperty("Target").GetString());
    }

    // A build for a job with no declared parameters reports an empty parameters object (not null/absent).
    [Fact]
    public async Task BuildDetail_ParameterlessBuild_ReturnsEmptyParametersObject_F1a()
    {
        await using var host = await HostProcess.StartAsync(new[] { Admin }, initialAdmins: new[] { "admin" });
        await HttpJson.PostAsync(host.Client, "/api/login", new { username = "admin", password = "admin123" });
        await HttpJson.PostAsync(host.Client, "/api/admin/jobs", new { name = "plain" });
        File.WriteAllText(Path.Combine(host.Root, "jobs", "plain", "pipeline.cipipe"),
            "Stage \"Work\" { Write-Host \"done\" }");

        var triggerRes = await host.Client.PostAsync("/api/jobs/plain/trigger", null);
        triggerRes.EnsureSuccessStatusCode();
        using var triggered = await HttpJson.ReadJsonAsync(triggerRes);
        var buildId = triggered.RootElement.GetProperty("id").GetInt64();

        await WaitForStatusAsync(host.Client, buildId, "success", TimeSpan.FromSeconds(20));

        using var buildDoc = await HttpJson.ReadJsonAsync(await host.Client.GetAsync($"/api/builds/{buildId}"));
        var used = buildDoc.RootElement.GetProperty("parameters");
        Assert.Equal(JsonValueKind.Object, used.ValueKind);
        Assert.Empty(used.EnumerateObject());
    }
}
