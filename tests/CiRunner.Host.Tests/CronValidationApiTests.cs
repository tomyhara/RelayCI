using System.Net;
using System.Text.Json;
using CiRunner.Host.Tests.Support;

namespace CiRunner.Host.Tests;

/// <summary>L3 tests for spec §5 F1b cron trigger config, ci-runner-test-spec.md TMR-005 ("不正な
/// cron 式" -> 設定 API がバリデーションエラーを返す) and the E2E-019 next-run preview API. Driven
/// through a real CiRunner.Host subprocess, matching the pattern established by
/// BuildActionApiTests/BuildParametersApiTests.</summary>
public class CronValidationApiTests
{
    private static readonly TestLocalUser Admin = new("admin", "admin123", "admin");

    // TMR-005: a malformed cron expression on job create is rejected with 400 and an error message
    // naming the offending expression, and no job is created.
    [Fact]
    public async Task CreateJob_WithInvalidCronExpression_Returns400_TMR005()
    {
        await using var host = await HostProcess.StartAsync(new[] { Admin }, initialAdmins: new[] { "admin" });
        await HttpJson.PostAsync(host.Client, "/api/login", new { username = "admin", password = "admin123" });

        var res = await HttpJson.PostAsync(host.Client, "/api/admin/jobs", new
        {
            name = "bad-cron-job",
            cronSchedules = new[] { "not a cron expression" },
        });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        using var body = await HttpJson.ReadJsonAsync(res);
        var error = body.RootElement.GetProperty("error").GetString();
        Assert.Contains("not a cron expression", error);

        var getRes = await host.Client.GetAsync("/api/admin/jobs/bad-cron-job");
        Assert.Equal(HttpStatusCode.NotFound, getRes.StatusCode);
    }

    // TMR-002/005: with several schedules declared, a single bad one among otherwise-valid ones still
    // fails the whole request (all-or-nothing config write).
    [Fact]
    public async Task CreateJob_WithOneInvalidAmongMultipleCronExpressions_Returns400_TMR002_TMR005()
    {
        await using var host = await HostProcess.StartAsync(new[] { Admin }, initialAdmins: new[] { "admin" });
        await HttpJson.PostAsync(host.Client, "/api/login", new { username = "admin", password = "admin123" });

        var res = await HttpJson.PostAsync(host.Client, "/api/admin/jobs", new
        {
            name = "multi-cron-job",
            cronSchedules = new[] { "0 3 * * *", "99 99 99 99 99" },
        });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var getRes = await host.Client.GetAsync("/api/admin/jobs/multi-cron-job");
        Assert.Equal(HttpStatusCode.NotFound, getRes.StatusCode);
    }

    // A valid single cron expression (and several valid ones together) is accepted and round-trips.
    [Fact]
    public async Task CreateJob_WithValidCronExpressions_Succeeds()
    {
        await using var host = await HostProcess.StartAsync(new[] { Admin }, initialAdmins: new[] { "admin" });
        await HttpJson.PostAsync(host.Client, "/api/login", new { username = "admin", password = "admin123" });

        var res = await HttpJson.PostAsync(host.Client, "/api/admin/jobs", new
        {
            name = "good-cron-job",
            cronSchedules = new[] { "0 3 * * *", "*/15 * * * *" },
        });

        res.EnsureSuccessStatusCode();
        using var body = await HttpJson.ReadJsonAsync(res);
        var schedules = JsonSerializer.Deserialize<List<string>>(body.RootElement.GetProperty("cronSchedules").GetString()!);
        Assert.Equal(new[] { "0 3 * * *", "*/15 * * * *" }, schedules);
    }

    // TMR-005: the same validation applies to job update, on an already-existing job.
    [Fact]
    public async Task UpdateJob_WithInvalidCronExpression_Returns400_LeavesExistingConfigUnchanged_TMR005()
    {
        await using var host = await HostProcess.StartAsync(new[] { Admin }, initialAdmins: new[] { "admin" });
        await HttpJson.PostAsync(host.Client, "/api/login", new { username = "admin", password = "admin123" });
        await HttpJson.PostAsync(host.Client, "/api/admin/jobs", new
        {
            name = "update-cron-job",
            cronSchedules = new[] { "0 3 * * *" },
        });

        var putContent = new StringContent(JsonSerializer.Serialize(new { cronSchedules = new[] { "0 3 * * *", "not-a-cron" } }));
        putContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        var putRes = await host.Client.PutAsync("/api/admin/jobs/update-cron-job", putContent);
        Assert.Equal(HttpStatusCode.BadRequest, putRes.StatusCode);

        using var getDoc = await HttpJson.ReadJsonAsync(await host.Client.GetAsync("/api/admin/jobs/update-cron-job"));
        var schedules = JsonSerializer.Deserialize<List<string>>(getDoc.RootElement.GetProperty("cronSchedules").GetString()!);
        Assert.Equal(new[] { "0 3 * * *" }, schedules);
    }

    // A valid cron update succeeds and is reflected back.
    [Fact]
    public async Task UpdateJob_WithValidCronExpression_Succeeds()
    {
        await using var host = await HostProcess.StartAsync(new[] { Admin }, initialAdmins: new[] { "admin" });
        await HttpJson.PostAsync(host.Client, "/api/login", new { username = "admin", password = "admin123" });
        await HttpJson.PostAsync(host.Client, "/api/admin/jobs", new { name = "update-cron-job-2" });

        var putContent = new StringContent(JsonSerializer.Serialize(new { cronSchedules = new[] { "*/5 * * * *" } }));
        putContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        var putRes = await host.Client.PutAsync("/api/admin/jobs/update-cron-job-2", putContent);
        putRes.EnsureSuccessStatusCode();

        using var getDoc = await HttpJson.ReadJsonAsync(await host.Client.GetAsync("/api/admin/jobs/update-cron-job-2"));
        var schedules = JsonSerializer.Deserialize<List<string>>(getDoc.RootElement.GetProperty("cronSchedules").GetString()!);
        Assert.Equal(new[] { "*/5 * * * *" }, schedules);
    }

    // E2E-019 (server side): GET /api/admin/cron/preview returns the next N occurrences for a valid
    // expression, and 400 with an error message for an invalid one.
    [Fact]
    public async Task CronPreview_ValidExpression_ReturnsUpcomingOccurrences()
    {
        await using var host = await HostProcess.StartAsync(new[] { Admin }, initialAdmins: new[] { "admin" });
        await HttpJson.PostAsync(host.Client, "/api/login", new { username = "admin", password = "admin123" });

        using var doc = await HttpJson.ReadJsonAsync(await host.Client.GetAsync("/api/admin/cron/preview?expr=" + Uri.EscapeDataString("*/5 * * * *")));
        var occurrences = doc.RootElement.GetProperty("occurrences");
        Assert.True(occurrences.GetArrayLength() >= 1);
        // Each entry parses as a real, strictly-increasing timestamp.
        DateTimeOffset? previous = null;
        foreach (var el in occurrences.EnumerateArray())
        {
            var ts = DateTimeOffset.Parse(el.GetString()!);
            if (previous is not null) Assert.True(ts > previous);
            previous = ts;
        }
    }

    [Fact]
    public async Task CronPreview_InvalidExpression_Returns400WithError()
    {
        await using var host = await HostProcess.StartAsync(new[] { Admin }, initialAdmins: new[] { "admin" });
        await HttpJson.PostAsync(host.Client, "/api/login", new { username = "admin", password = "admin123" });

        var res = await host.Client.GetAsync("/api/admin/cron/preview?expr=" + Uri.EscapeDataString("garbage"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        using var doc = await HttpJson.ReadJsonAsync(res);
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("error").GetString()));
    }

    // E2E-019 (server side): GET /api/jobs surfaces nextRunAt for a job with a cron schedule, and null
    // for one without.
    [Fact]
    public async Task JobsList_IncludesNextRunAt_ForCronScheduledJob_NullOtherwise()
    {
        await using var host = await HostProcess.StartAsync(new[] { Admin }, initialAdmins: new[] { "admin" });
        await HttpJson.PostAsync(host.Client, "/api/login", new { username = "admin", password = "admin123" });
        await HttpJson.PostAsync(host.Client, "/api/admin/jobs", new
        {
            name = "cron-listed-job",
            cronSchedules = new[] { "*/5 * * * *" },
        });
        await HttpJson.PostAsync(host.Client, "/api/admin/jobs", new { name = "no-cron-job" });

        using var doc = await HttpJson.ReadJsonAsync(await host.Client.GetAsync("/api/jobs"));
        var cronJob = doc.RootElement.EnumerateArray().First(j => j.GetProperty("name").GetString() == "cron-listed-job");
        var plainJob = doc.RootElement.EnumerateArray().First(j => j.GetProperty("name").GetString() == "no-cron-job");

        Assert.NotEqual(JsonValueKind.Null, cronJob.GetProperty("nextRunAt").ValueKind);
        var nextRun = DateTimeOffset.Parse(cronJob.GetProperty("nextRunAt").GetString()!);
        Assert.True(nextRun > DateTimeOffset.Now);

        Assert.Equal(JsonValueKind.Null, plainJob.GetProperty("nextRunAt").ValueKind);
    }
}
