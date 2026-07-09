using CiRunner.Core.Models;
using CiRunner.Core.Tests.Support;
using Xunit;

namespace CiRunner.Core.Tests;

/// <summary>Retention policy tests (ci-runner-test-spec.md §3.3 ENG-070).</summary>
public class RetentionServiceTests
{
    private static BuildRecord FinishBuild(EngineFixture fx, long jobId)
    {
        var build = fx.Builds.CreateQueued(jobId, BuildTrigger.Manual, "{}", null);
        fx.Builds.UpsertStepStart(build.Id, 1, "Step", null);
        fx.Builds.UpdateStepEnd(build.Id, 1, StepStatus.Success, null);
        fx.Builds.UpdateStatus(build.Id, BuildStatus.Success, startedAt: DateTimeOffset.Now.ToString("o"), finishedAt: DateTimeOffset.Now.ToString("o"));
        return fx.Builds.FindById(build.Id)!;
    }

    [Fact]
    public void Enforce_RetentionThree_FiveBuilds_DeletesOldestTwo_ENG070()
    {
        using var fx = new EngineFixture();
        var job = fx.CreateJob("j", "Stage \"A\" { Write-Host 1 }");
        fx.Jobs.UpsertConfiguredJob(new JobConfigInput(
            Name: "j", RepoUrl: null, WorkspacePath: null, PipelineSource: "server", PipelinePath: "pipeline.cipipe",
            ParametersJson: "[]", CronSchedulesJson: "[]", PollingBranchesJson: null, ResourcesJson: "[]",
            QueuePolicy: "replace", TimeoutMinutes: null, Retention: 3, ShellPath: null, Enabled: true));
        job = fx.Jobs.FindByName("j")!;

        var logDir = fx.Paths.JobLogsDir("j");
        Directory.CreateDirectory(logDir);
        var builds = new List<BuildRecord>();
        for (var i = 0; i < 5; i++)
        {
            var b = FinishBuild(fx, job.Id);
            File.WriteAllText(fx.Paths.BuildLogPath("j", b.Number), "log content");
            builds.Add(b);
        }

        fx.Retention.Enforce(job);

        var remaining = fx.Builds.ListByJob(job.Id, 100);
        Assert.Equal(3, remaining.Count);
        // Newest 3 (numbers 5,4,3) survive; oldest 2 (1,2) are gone.
        Assert.Equal(new[] { 5, 4, 3 }, remaining.Select(b => b.Number));

        Assert.Null(fx.Builds.FindById(builds[0].Id));
        Assert.Null(fx.Builds.FindById(builds[1].Id));
        Assert.NotNull(fx.Builds.FindById(builds[2].Id));

        Assert.False(File.Exists(fx.Paths.BuildLogPath("j", 1)));
        Assert.False(File.Exists(fx.Paths.BuildLogPath("j", 2)));
        Assert.True(File.Exists(fx.Paths.BuildLogPath("j", 3)));
    }

    [Fact]
    public void Enforce_UsesSystemDefault_WhenJobRetentionUnset()
    {
        using var fx = new EngineFixture();
        var job = fx.CreateJob("j", "Stage \"A\" { Write-Host 1 }");
        fx.Settings.Set("defaultRetention", "2");

        for (var i = 0; i < 4; i++)
        {
            FinishBuild(fx, job.Id);
        }

        fx.Retention.Enforce(job);

        var remaining = fx.Builds.ListByJob(job.Id, 100);
        Assert.Equal(2, remaining.Count);
    }

    [Fact]
    public void Enforce_NeverDeletesQueuedOrRunningBuilds_EvenWhenOlderThanRetentionWindow()
    {
        using var fx = new EngineFixture();
        var job = fx.CreateJob("j", "Stage \"A\" { Write-Host 1 }");
        fx.Jobs.UpsertConfiguredJob(new JobConfigInput(
            Name: "j", RepoUrl: null, WorkspacePath: null, PipelineSource: "server", PipelinePath: "pipeline.cipipe",
            ParametersJson: "[]", CronSchedulesJson: "[]", PollingBranchesJson: null, ResourcesJson: "[]",
            QueuePolicy: "replace", TimeoutMinutes: null, Retention: 1, ShellPath: null, Enabled: true));
        job = fx.Jobs.FindByName("j")!;

        // Build #1 stays Queued (e.g. still waiting on a resource) while #2-#4 finish around it, so
        // by number it's well outside a retention=1 window - it must survive the sweep anyway.
        var stillQueued = fx.Builds.CreateQueued(job.Id, BuildTrigger.Manual, "{}", null);
        FinishBuild(fx, job.Id);
        FinishBuild(fx, job.Id);
        var newest = FinishBuild(fx, job.Id);

        fx.Retention.Enforce(job);

        Assert.NotNull(fx.Builds.FindById(stillQueued.Id));
        Assert.NotNull(fx.Builds.FindById(newest.Id));
        var remaining = fx.Builds.ListByJob(job.Id, 100);
        Assert.Equal(2, remaining.Count); // the queued one + the newest terminal one; #2/#3 swept
    }
}
