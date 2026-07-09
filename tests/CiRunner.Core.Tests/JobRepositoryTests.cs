using CiRunner.Core.Data;
using CiRunner.Core.Models;
using CiRunner.Core.Tests.Support;
using Xunit;

namespace CiRunner.Core.Tests;

public class JobRepositoryTests
{
    [Fact]
    public void UpsertServerJob_CreatesJobWithServerDefaults()
    {
        using var temp = new TempDatabase();
        var repo = new JobRepository(temp.Db);

        var job = repo.UpsertServerJob("hello");

        Assert.Equal("hello", job.Name);
        Assert.Equal("server", job.PipelineSource);
        Assert.True(job.Enabled);
        Assert.False(job.Deleted);
    }

    [Fact]
    public void UpsertServerJob_CalledTwice_IsIdempotent()
    {
        using var temp = new TempDatabase();
        var repo = new JobRepository(temp.Db);

        var first = repo.UpsertServerJob("hello");
        var second = repo.UpsertServerJob("hello");

        Assert.Equal(first.Id, second.Id);
        Assert.Single(repo.ListEnabled());
    }

    [Fact]
    public void FindByName_UnknownJob_ReturnsNull()
    {
        using var temp = new TempDatabase();
        var repo = new JobRepository(temp.Db);

        Assert.Null(repo.FindByName("does-not-exist"));
    }

    [Fact]
    public void ListEnabled_ReturnsAllRegisteredJobsSortedByName()
    {
        using var temp = new TempDatabase();
        var repo = new JobRepository(temp.Db);
        repo.UpsertServerJob("zeta");
        repo.UpsertServerJob("alpha");

        var names = repo.ListEnabled().Select(j => j.Name).ToList();

        Assert.Equal(new[] { "alpha", "zeta" }, names);
    }

    // F6: admin job deletion (spec §5 F6 "ジョブ削除は論理削除")
    [Fact]
    public void SoftDelete_HidesJobFromFindByNameAndListEnabled()
    {
        using var temp = new TempDatabase();
        var repo = new JobRepository(temp.Db);
        repo.UpsertServerJob("to-delete");

        var deleted = repo.SoftDelete("to-delete");

        Assert.True(deleted);
        Assert.Null(repo.FindByName("to-delete"));
        Assert.Empty(repo.ListEnabled());
    }

    [Fact]
    public void SoftDelete_UnknownJob_ReturnsFalse()
    {
        using var temp = new TempDatabase();
        var repo = new JobRepository(temp.Db);

        Assert.False(repo.SoftDelete("does-not-exist"));
    }

    [Fact]
    public void SoftDelete_ThenReUpsertConfiguredJob_StaysDeleted()
    {
        // Mirrors what happens on a restart: JobScanner re-applies jobs/<name>/job.json for every
        // pipeline.cipipe it finds on disk, regardless of the DB's soft-delete state. UpsertConfiguredJob
        // must never resurrect a deleted row purely by being called again with the same name.
        using var temp = new TempDatabase();
        var repo = new JobRepository(temp.Db);
        repo.UpsertServerJob("to-delete");
        repo.SoftDelete("to-delete");

        repo.UpsertConfiguredJob(new JobConfigInput(
            Name: "to-delete", RepoUrl: null, WorkspacePath: null, PipelineSource: "server", PipelinePath: "pipeline.cipipe",
            ParametersJson: "[]", CronSchedulesJson: "[]", PollingBranchesJson: null, ResourcesJson: "[]",
            QueuePolicy: "replace", TimeoutMinutes: null, Retention: null, ShellPath: null, Enabled: true));

        Assert.Null(repo.FindByName("to-delete"));
    }
}
