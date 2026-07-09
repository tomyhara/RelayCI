using CiRunner.Core.Data;
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
}
