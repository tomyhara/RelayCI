using CiRunner.Core.Data;
using CiRunner.Core.Models;
using CiRunner.Core.Tests.Support;
using Xunit;

namespace CiRunner.Core.Tests;

public class BuildRepositoryTests
{
    private static (JobRepository jobs, BuildRepository builds) CreateRepos(TempDatabase temp) =>
        (new JobRepository(temp.Db), new BuildRepository(temp.Db));

    [Fact]
    public void CreateQueued_AssignsIncrementingNumbersPerJob()
    {
        using var temp = new TempDatabase();
        var (jobs, builds) = CreateRepos(temp);
        var job = jobs.UpsertServerJob("hello");

        var b1 = builds.CreateQueued(job.Id, BuildTrigger.Manual, "{}", null);
        var b2 = builds.CreateQueued(job.Id, BuildTrigger.Manual, "{}", null);

        Assert.Equal(1, b1.Number);
        Assert.Equal(2, b2.Number);
        Assert.Equal(BuildStatus.Queued, b1.Status);
    }

    [Fact]
    public void CreateQueued_NumbersAreIndependentPerJob()
    {
        using var temp = new TempDatabase();
        var (jobs, builds) = CreateRepos(temp);
        var jobA = jobs.UpsertServerJob("a");
        var jobB = jobs.UpsertServerJob("b");

        builds.CreateQueued(jobA.Id, BuildTrigger.Manual, "{}", null);
        var bB1 = builds.CreateQueued(jobB.Id, BuildTrigger.Manual, "{}", null);

        Assert.Equal(1, bB1.Number);
    }

    [Fact]
    public void ListQueued_OnlyReturnsQueuedBuilds()
    {
        using var temp = new TempDatabase();
        var (jobs, builds) = CreateRepos(temp);
        var job = jobs.UpsertServerJob("hello");
        var b1 = builds.CreateQueued(job.Id, BuildTrigger.Manual, "{}", null);
        var b2 = builds.CreateQueued(job.Id, BuildTrigger.Manual, "{}", null);
        builds.UpdateStatus(b1.Id, BuildStatus.Running, startedAt: DateTimeOffset.Now.ToString("o"));

        var queued = builds.ListQueued();

        var single = Assert.Single(queued);
        Assert.Equal(b2.Id, single.Id);
    }

    [Fact]
    public void UpdateStatus_SetsStartedAndFinishedTimestamps()
    {
        using var temp = new TempDatabase();
        var (jobs, builds) = CreateRepos(temp);
        var job = jobs.UpsertServerJob("hello");
        var build = builds.CreateQueued(job.Id, BuildTrigger.Manual, "{}", null);

        builds.UpdateStatus(build.Id, BuildStatus.Running, startedAt: "2026-01-01T00:00:00+09:00");
        builds.UpdateStatus(build.Id, BuildStatus.Success, finishedAt: "2026-01-01T00:01:00+09:00");

        var reloaded = builds.FindById(build.Id)!;
        Assert.Equal(BuildStatus.Success, reloaded.Status);
        Assert.Equal("2026-01-01T00:00:00+09:00", reloaded.StartedAt);
        Assert.Equal("2026-01-01T00:01:00+09:00", reloaded.FinishedAt);
    }

    [Fact]
    public void StepLifecycle_StartEndAndOffsets_AreRecordedInOrder()
    {
        using var temp = new TempDatabase();
        var (jobs, builds) = CreateRepos(temp);
        var job = jobs.UpsertServerJob("hello");
        var build = builds.CreateQueued(job.Id, BuildTrigger.Manual, "{}", null);

        builds.UpsertStepStart(build.Id, 1, "Build", post: null);
        builds.UpdateStepLogOffsets(build.Id, 1, offsetStart: 0, offsetEnd: null);
        builds.UpdateStepEnd(build.Id, 1, StepStatus.Success, error: null);
        builds.UpdateStepLogOffsets(build.Id, 1, offsetStart: null, offsetEnd: 42);

        builds.UpsertStepStart(build.Id, 2, "Test", post: null);
        builds.UpdateStepEnd(build.Id, 2, StepStatus.Failed, error: "boom");

        var steps = builds.ListSteps(build.Id);

        Assert.Equal(2, steps.Count);
        Assert.Equal("Build", steps[0].Name);
        Assert.Equal(StepStatus.Success, steps[0].Status);
        Assert.Equal(0, steps[0].LogOffsetStart);
        Assert.Equal(42, steps[0].LogOffsetEnd);
        Assert.Equal("Test", steps[1].Name);
        Assert.Equal(StepStatus.Failed, steps[1].Status);
        Assert.Equal("boom", steps[1].Error);
    }

    [Fact]
    public void SetNote_UpdatesBuildNote()
    {
        using var temp = new TempDatabase();
        var (jobs, builds) = CreateRepos(temp);
        var job = jobs.UpsertServerJob("hello");
        var build = builds.CreateQueued(job.Id, BuildTrigger.Manual, "{}", null);

        builds.SetNote(build.Id, "hello note");

        Assert.Equal("hello note", builds.FindById(build.Id)!.Note);
    }

    [Fact]
    public void FindLatestByJob_ReturnsHighestNumberedBuild()
    {
        using var temp = new TempDatabase();
        var (jobs, builds) = CreateRepos(temp);
        var job = jobs.UpsertServerJob("hello");
        builds.CreateQueued(job.Id, BuildTrigger.Manual, "{}", null);
        var b2 = builds.CreateQueued(job.Id, BuildTrigger.Manual, "{}", null);

        var latest = builds.FindLatestByJob(job.Id);

        Assert.Equal(b2.Id, latest!.Id);
    }
}
