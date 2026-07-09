using CiRunner.Core.Models;
using CiRunner.Core.Tests.Support;
using Xunit;

namespace CiRunner.Core.Tests;

/// <summary>DedupKey / queue_policy tests (ci-runner-test-spec.md §3.4, WH-006 / PRM-004 area).</summary>
public class JobTriggerServiceTests
{
    [Fact]
    public void Trigger_UnknownJob_ReturnsNotQueued()
    {
        using var fx = new EngineFixture();
        var result = fx.TriggerService.Trigger("does-not-exist", BuildTrigger.Manual, null, dedupKey: null);
        Assert.False(result.Queued);
        Assert.Equal("job-not-found-or-disabled", result.Reason);
    }

    [Fact]
    public void Trigger_NoDedupKey_AlwaysQueuesANewBuild()
    {
        using var fx = new EngineFixture();
        var job = fx.CreateJob("j", "Stage \"A\" { Write-Host 1 }");

        var r1 = fx.TriggerService.Trigger("j", BuildTrigger.Manual, null, dedupKey: null);
        var r2 = fx.TriggerService.Trigger("j", BuildTrigger.Manual, null, dedupKey: null);

        Assert.True(r1.Queued);
        Assert.True(r2.Queued);
        Assert.NotEqual(r1.Build!.Id, r2.Build!.Id);
        Assert.Equal(2, fx.Builds.ListQueued().Count);
    }

    [Fact]
    public void Trigger_SameDedupKey_ReplacePolicy_DiscardsQueuedDuplicate()
    {
        using var fx = new EngineFixture();
        // Default queue_policy is 'replace' (jobs table default) for jobs registered without job.json.
        var job = fx.CreateJob("j", "Stage \"A\" { Write-Host 1 }");
        Assert.Equal("replace", job.QueuePolicy);

        var r1 = fx.TriggerService.Trigger("j", BuildTrigger.Polling, null, dedupKey: "sha-1");
        var r2 = fx.TriggerService.Trigger("j", BuildTrigger.Polling, null, dedupKey: "sha-1");

        Assert.True(r1.Queued);
        Assert.True(r2.Queued);
        Assert.NotEqual(r1.Build!.Id, r2.Build!.Id);

        var stillQueued = fx.Builds.ListQueued();
        var onlyBuild = Assert.Single(stillQueued);
        Assert.Equal(r2.Build.Id, onlyBuild.Id);

        var discarded = fx.Builds.FindById(r1.Build.Id)!;
        Assert.Equal(BuildStatus.Aborted, discarded.Status);
    }

    [Fact]
    public void Trigger_SameDedupKey_QueuePolicy_KeepsBothQueued()
    {
        using var fx = new EngineFixture();
        var job = fx.CreateJob("j", "Stage \"A\" { Write-Host 1 }");
        fx.Jobs.UpsertConfiguredJob(new JobConfigInput(
            Name: "j", RepoUrl: null, WorkspacePath: null, PipelineSource: "server", PipelinePath: "pipeline.cipipe",
            ParametersJson: "[]", CronSchedulesJson: "[]", PollingBranchesJson: null, ResourcesJson: "[]",
            QueuePolicy: "queue", TimeoutMinutes: null, Retention: null, ShellPath: null, Enabled: true));

        var r1 = fx.TriggerService.Trigger("j", BuildTrigger.Polling, null, dedupKey: "sha-1");
        var r2 = fx.TriggerService.Trigger("j", BuildTrigger.Polling, null, dedupKey: "sha-1");

        Assert.True(r1.Queued);
        Assert.True(r2.Queued);
        Assert.Equal(2, fx.Builds.ListQueued().Count);
    }

    [Fact]
    public async Task Trigger_SameDedupKeyAsRunningBuild_IsSkippedAsDedup()
    {
        using var fx = new EngineFixture(executorLimit: 1);
        fx.CreateJob("j", "Stage \"A\" { Start-Sleep -Milliseconds 800 }");
        await fx.Dispatcher.StartAsync(CancellationToken.None);
        try
        {
            var r1 = fx.TriggerService.Trigger("j", BuildTrigger.Polling, null, dedupKey: "sha-1");
            fx.Dispatcher.Signal();

            // Wait until r1 is actually Running before firing the duplicate.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (fx.Builds.FindById(r1.Build!.Id)!.Status != BuildStatus.Running && !cts.IsCancellationRequested)
            {
                await Task.Delay(50, CancellationToken.None);
            }
            Assert.Equal(BuildStatus.Running, fx.Builds.FindById(r1.Build!.Id)!.Status);

            var r2 = fx.TriggerService.Trigger("j", BuildTrigger.Polling, null, dedupKey: "sha-1");

            Assert.False(r2.Queued);
            Assert.Equal("dedup", r2.Reason);
        }
        finally
        {
            await fx.Dispatcher.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public void Trigger_RequiredParameterMissing_IsNotQueued()
    {
        using var fx = new EngineFixture();
        fx.CreateJob("j", "Stage \"A\" { Write-Host 1 }");
        fx.Jobs.UpsertConfiguredJob(new JobConfigInput(
            Name: "j", RepoUrl: null, WorkspacePath: null, PipelineSource: "server", PipelinePath: "pipeline.cipipe",
            ParametersJson: """[{"Name":"Required1","Required":true}]""", CronSchedulesJson: "[]",
            PollingBranchesJson: null, ResourcesJson: "[]", QueuePolicy: "replace", TimeoutMinutes: null,
            Retention: null, ShellPath: null, Enabled: true));

        var result = fx.TriggerService.Trigger("j", BuildTrigger.Manual, null, dedupKey: null);

        Assert.False(result.Queued);
        Assert.Contains("Required1", result.Reason);
    }

    // PRM-003: Rebuild re-queues the same job with the same parameters and (for repo jobs) the same
    // commit/branch, tagged trigger=rebuild.
    [Fact]
    public void Rebuild_UnknownBuild_ReturnsNotQueued()
    {
        using var fx = new EngineFixture();
        var result = fx.TriggerService.Rebuild(999);
        Assert.False(result.Queued);
        Assert.Equal("build-not-found", result.Reason);
    }

    [Fact]
    public void Rebuild_SameParametersAndCommit_QueuesNewBuildTaggedRebuild()
    {
        using var fx = new EngineFixture();
        fx.CreateJob("j", "Stage \"A\" { Write-Host 1 }");
        fx.Jobs.UpsertConfiguredJob(new JobConfigInput(
            Name: "j", RepoUrl: "https://example.invalid/repo.git", WorkspacePath: null, PipelineSource: "server",
            PipelinePath: "pipeline.cipipe", ParametersJson: """[{"Name":"Target","Default":"x"}]""",
            CronSchedulesJson: "[]", PollingBranchesJson: null, ResourcesJson: "[]", QueuePolicy: "queue",
            TimeoutMinutes: null, Retention: null, ShellPath: null, Enabled: true));

        var original = fx.TriggerService.Trigger("j", BuildTrigger.Manual, new Dictionary<string, string> { ["Target"] = "release" }, dedupKey: null);
        Assert.True(original.Queued);
        fx.Builds.SetCommitInfo(original.Build!.Id, "abc123", "main");

        var rebuilt = fx.TriggerService.Rebuild(original.Build.Id);

        Assert.True(rebuilt.Queued);
        Assert.NotEqual(original.Build.Id, rebuilt.Build!.Id);
        Assert.Equal(BuildTrigger.Rebuild, rebuilt.Build.Trigger);
        Assert.Equal("abc123", rebuilt.Build.CommitSha);
        Assert.Equal("main", rebuilt.Build.Branch);
        Assert.Equal(fx.Builds.FindById(original.Build.Id)!.Parameters, rebuilt.Build.Parameters);
    }

    [Fact]
    public void Rebuild_DisabledJob_ReturnsNotQueued()
    {
        using var fx = new EngineFixture();
        fx.CreateJob("j", "Stage \"A\" { Write-Host 1 }");
        var original = fx.TriggerService.Trigger("j", BuildTrigger.Manual, null, dedupKey: null);
        Assert.True(original.Queued);

        fx.Jobs.UpsertConfiguredJob(new JobConfigInput(
            Name: "j", RepoUrl: null, WorkspacePath: null, PipelineSource: "server", PipelinePath: "pipeline.cipipe",
            ParametersJson: "[]", CronSchedulesJson: "[]", PollingBranchesJson: null, ResourcesJson: "[]",
            QueuePolicy: "replace", TimeoutMinutes: null, Retention: null, ShellPath: null, Enabled: false));

        var rebuilt = fx.TriggerService.Rebuild(original.Build!.Id);

        Assert.False(rebuilt.Queued);
        Assert.Equal("job-not-found-or-disabled", rebuilt.Reason);
    }
}
