using CiRunner.Core.Models;
using CiRunner.Core.Tests.Support;
using Xunit;

namespace CiRunner.Core.Tests;

/// <summary>
/// L3 integration tests: real powershell.exe running the real bootstrap.ps1 + CiRunner.psm1 against a
/// real SQLite DB and workspace, mirroring ci-runner-test-spec.md §3.1 (ENG-*) for the M1 slice.
/// </summary>
public class BuildRunnerIntegrationTests
{
    [Fact]
    public async Task RunAsync_SuccessfulPipeline_RecordsSuccessAndSteps()
    {
        using var fx = new EngineFixture();
        var job = fx.CreateJob("hello", """
            Stage "Build" { Write-Host "building" }
            Stage "Test" { Write-Host "testing" }
            """);
        var build = fx.Builds.CreateQueued(job.Id, BuildTrigger.Manual, "{}", null);

        await fx.Runner.RunAsync(job, build, CancellationToken.None);

        var reloaded = fx.Builds.FindById(build.Id)!;
        Assert.Equal(BuildStatus.Success, reloaded.Status);
        var steps = fx.Builds.ListSteps(build.Id);
        Assert.Equal(2, steps.Count);
        Assert.All(steps, s => Assert.Equal(StepStatus.Success, s.Status));
    }

    [Fact]
    public async Task RunAsync_FailingStage_RecordsFailedStatusAndError()
    {
        using var fx = new EngineFixture();
        var job = fx.CreateJob("failer", """
            Stage "Boom" { throw "kaboom" }
            """);
        var build = fx.Builds.CreateQueued(job.Id, BuildTrigger.Manual, "{}", null);

        await fx.Runner.RunAsync(job, build, CancellationToken.None);

        var reloaded = fx.Builds.FindById(build.Id)!;
        Assert.Equal(BuildStatus.Failed, reloaded.Status);
        var step = Assert.Single(fx.Builds.ListSteps(build.Id));
        Assert.Equal(StepStatus.Failed, step.Status);
        Assert.Contains("kaboom", step.Error);
    }

    [Fact]
    public async Task RunAsync_MissingPipelineFile_RecordsFailedWithoutSteps()
    {
        using var fx = new EngineFixture();
        // CreateJob normally writes pipeline.cipipe; register the job row directly without the file.
        System.IO.Directory.CreateDirectory(fx.Paths.JobDir("nofile"));
        var job = fx.Jobs.UpsertServerJob("nofile");
        var build = fx.Builds.CreateQueued(job.Id, BuildTrigger.Manual, "{}", null);

        await fx.Runner.RunAsync(job, build, CancellationToken.None);

        var reloaded = fx.Builds.FindById(build.Id)!;
        Assert.Equal(BuildStatus.Failed, reloaded.Status);
        Assert.Empty(fx.Builds.ListSteps(build.Id));
    }

    [Fact]
    public async Task RunAsync_RepoLessJob_DoesNotSetCommitShaOrBranch_PRM001()
    {
        using var fx = new EngineFixture();
        var job = fx.CreateJob("norepo", """
            Stage "Check" {
                if ($env:CI_COMMIT_SHA -or $env:CI_BRANCH) { throw "should be unset for repo-less job" }
            }
            """);
        var build = fx.Builds.CreateQueued(job.Id, BuildTrigger.Manual, "{}", null);

        await fx.Runner.RunAsync(job, build, CancellationToken.None);

        Assert.Equal(BuildStatus.Success, fx.Builds.FindById(build.Id)!.Status);
        Assert.True(System.IO.Directory.Exists(fx.Paths.JobWorkspaceDir("norepo")));
    }

    [Fact]
    public async Task RunAsync_InjectsCoreEnvironmentVariables_ENG033()
    {
        using var fx = new EngineFixture();
        var job = fx.CreateJob("envcheck", """
            Stage "Check" {
                if (-not $env:CI_JOB_NAME) { throw "CI_JOB_NAME missing" }
                if (-not $env:CI_BUILD_NUMBER) { throw "CI_BUILD_NUMBER missing" }
                if (-not $env:CI_BUILD_ID) { throw "CI_BUILD_ID missing" }
                if ($env:CI_TRIGGER -ne 'manual') { throw "CI_TRIGGER wrong: $env:CI_TRIGGER" }
                if (-not $env:CI_WORKSPACE) { throw "CI_WORKSPACE missing" }
                if (-not $env:CI_CONTROL_FILE) { throw "CI_CONTROL_FILE missing" }
                if (-not $env:CI_RESULT_DIR) { throw "CI_RESULT_DIR missing" }
                if (-not $env:CI_ARTIFACT_DIR) { throw "CI_ARTIFACT_DIR missing" }
                if ($env:CI_JOB_NAME -ne 'envcheck') { throw "CI_JOB_NAME wrong: $env:CI_JOB_NAME" }
            }
            """);
        var build = fx.Builds.CreateQueued(job.Id, BuildTrigger.Manual, "{}", null);

        await fx.Runner.RunAsync(job, build, CancellationToken.None);

        Assert.Equal(BuildStatus.Success, fx.Builds.FindById(build.Id)!.Status);
    }

    [Fact]
    public async Task RunAsync_StepLogOffsets_AreMonotonicAndBounded_ENG040()
    {
        // Offsets are captured from the control-file tailer's poll cadence, independent of the
        // stdout pump's cadence - DSL spec §4.3 explicitly accepts drift between the two ("数十ms
        // 程度の位置ずれが生じうるが...許容") and says banner lines, not offsets, are for human
        // readability. So this asserts what's actually guaranteed (well-formed, non-decreasing,
        // in-bounds offsets covering the whole log) rather than exact per-stage content boundaries.
        using var fx = new EngineFixture();
        var job = fx.CreateJob("offsets", """
            Stage "First" { Write-Host "FIRST-MARKER"; Start-Sleep -Milliseconds 300 }
            Stage "Second" { Write-Host "SECOND-MARKER"; Start-Sleep -Milliseconds 300 }
            """);
        var build = fx.Builds.CreateQueued(job.Id, BuildTrigger.Manual, "{}", null);

        await fx.Runner.RunAsync(job, build, CancellationToken.None);

        var logPath = fx.Paths.BuildLogPath("offsets", build.Number);
        var logLength = new System.IO.FileInfo(logPath).Length;
        var logText = await System.IO.File.ReadAllTextAsync(logPath);
        var steps = fx.Builds.ListSteps(build.Id);
        Assert.Equal(2, steps.Count);

        Assert.Contains("FIRST-MARKER", logText);
        Assert.Contains("SECOND-MARKER", logText);

        foreach (var step in steps)
        {
            Assert.InRange(step.LogOffsetStart!.Value, 0, logLength);
            Assert.InRange(step.LogOffsetEnd!.Value, step.LogOffsetStart!.Value, logLength);
        }
        Assert.True(steps[1].LogOffsetStart >= steps[0].LogOffsetStart, "Step offsets must be non-decreasing across stages.");
        Assert.Equal(logLength, steps[1].LogOffsetEnd);
    }

    [Fact]
    public async Task RunAsync_PostStageAlways_RunsEvenAfterFailure()
    {
        using var fx = new EngineFixture();
        var job = fx.CreateJob("withpost", """
            PostStage "Cleanup" { Write-Host "cleanup ran" }
            Stage "Boom" { throw "fail" }
            """);
        var build = fx.Builds.CreateQueued(job.Id, BuildTrigger.Manual, "{}", null);

        await fx.Runner.RunAsync(job, build, CancellationToken.None);

        Assert.Equal(BuildStatus.Failed, fx.Builds.FindById(build.Id)!.Status);
        var steps = fx.Builds.ListSteps(build.Id);
        Assert.Equal(2, steps.Count);
        Assert.Equal("Cleanup", steps[1].Name);
        Assert.Equal("always", steps[1].Post);
        Assert.Equal(StepStatus.Success, steps[1].Status);
    }
}
