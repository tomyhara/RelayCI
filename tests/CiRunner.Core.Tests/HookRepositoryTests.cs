using CiRunner.Core.Data;
using CiRunner.Core.Tests.Support;
using Xunit;

namespace CiRunner.Core.Tests;

public class HookRepositoryTests
{
    [Fact]
    public void UpsertDiscoveredHook_CreatesHook()
    {
        using var temp = new TempDatabase();
        var repo = new HookRepository(temp.Db);

        var hook = repo.UpsertDiscoveredHook("gh", "hooks/gh.cipipe", "s3cr3t", 30, true);

        Assert.Equal("gh", hook.Name);
        Assert.Equal("s3cr3t", hook.Secret);
        Assert.Equal(30, hook.TimeoutSec);
        Assert.True(hook.Enabled);
        Assert.False(hook.Deleted);
    }

    [Fact]
    public void UpsertDiscoveredHook_CalledTwice_UpdatesInPlace()
    {
        using var temp = new TempDatabase();
        var repo = new HookRepository(temp.Db);
        var first = repo.UpsertDiscoveredHook("gh", "hooks/gh.cipipe", null, 60, true);

        var second = repo.UpsertDiscoveredHook("gh", "hooks/gh.cipipe", "new-secret", 45, false);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal("new-secret", second.Secret);
        Assert.Equal(45, second.TimeoutSec);
        Assert.False(second.Enabled);
    }

    // F6: admin hook deletion (spec §5 F6 "フック管理...無効化・削除")
    [Fact]
    public void SoftDelete_HidesHookFromFindByNameAndListEnabled()
    {
        using var temp = new TempDatabase();
        var repo = new HookRepository(temp.Db);
        repo.UpsertDiscoveredHook("gh", "hooks/gh.cipipe", null, 60, true);

        var deleted = repo.SoftDelete("gh");

        Assert.True(deleted);
        Assert.Null(repo.FindByName("gh"));
        Assert.Empty(repo.ListEnabled());
    }

    [Fact]
    public void SoftDelete_ThenReUpsert_StaysDeleted()
    {
        // Mirrors HookScanner re-applying hooks/<name>.json on every restart regardless of DB state.
        using var temp = new TempDatabase();
        var repo = new HookRepository(temp.Db);
        repo.UpsertDiscoveredHook("gh", "hooks/gh.cipipe", null, 60, true);
        repo.SoftDelete("gh");

        repo.UpsertDiscoveredHook("gh", "hooks/gh.cipipe", null, 60, true);

        Assert.Null(repo.FindByName("gh"));
    }

    [Fact]
    public void SoftDelete_UnknownHook_ReturnsFalse()
    {
        using var temp = new TempDatabase();
        var repo = new HookRepository(temp.Db);

        Assert.False(repo.SoftDelete("does-not-exist"));
    }
}
