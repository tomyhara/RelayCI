using CiRunner.Core.Data;
using CiRunner.Core.Tests.Support;
using Xunit;

namespace CiRunner.Core.Tests;

public class ResourceDefRepositoryTests
{
    [Fact]
    public void Upsert_CreatesThenUpdatesDescription()
    {
        using var temp = new TempDatabase();
        var repo = new ResourceDefRepository(temp.Db);

        repo.Upsert("bench-1", "HIL bench #1", "admin");
        var updated = repo.Upsert("bench-1", "HIL bench #1 (rack A)", "admin2");

        Assert.Equal("HIL bench #1 (rack A)", updated.Description);
        Assert.Equal("admin2", updated.UpdatedBy);
        Assert.Single(repo.ListAll());
    }

    [Fact]
    public void ListAll_ReturnsSortedByName()
    {
        using var temp = new TempDatabase();
        var repo = new ResourceDefRepository(temp.Db);
        repo.Upsert("zeta", null, "admin");
        repo.Upsert("alpha", null, "admin");

        var names = repo.ListAll().Select(r => r.Name).ToList();

        Assert.Equal(new[] { "alpha", "zeta" }, names);
    }

    [Fact]
    public void Delete_RemovesEntry()
    {
        using var temp = new TempDatabase();
        var repo = new ResourceDefRepository(temp.Db);
        repo.Upsert("bench-1", "desc", "admin");

        var deleted = repo.Delete("bench-1");

        Assert.True(deleted);
        Assert.Empty(repo.ListAll());
    }

    [Fact]
    public void Delete_UnknownName_ReturnsFalse()
    {
        using var temp = new TempDatabase();
        var repo = new ResourceDefRepository(temp.Db);

        Assert.False(repo.Delete("does-not-exist"));
    }
}
