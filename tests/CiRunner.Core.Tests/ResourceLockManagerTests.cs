using CiRunner.Core.Engine;
using Xunit;

namespace CiRunner.Core.Tests;

/// <summary>Pure unit tests for the F3a lock engine (ci-runner-test-spec.md §3.1 ENG-010/012).</summary>
public class ResourceLockManagerTests
{
    [Fact]
    public void TryAcquireAll_NoResources_AlwaysSucceeds()
    {
        var mgr = new ResourceLockManager();
        Assert.True(mgr.TryAcquireAll(1, Array.Empty<string>()));
    }

    [Fact]
    public void TryAcquireAll_FreeResource_Succeeds()
    {
        var mgr = new ResourceLockManager();
        Assert.True(mgr.TryAcquireAll(1, new[] { "bench-1" }));
        Assert.Equal(1, mgr.HolderOf("bench-1"));
    }

    [Fact]
    public void TryAcquireAll_AlreadyHeldByAnotherBuild_Fails()
    {
        var mgr = new ResourceLockManager();
        mgr.TryAcquireAll(1, new[] { "bench-1" });

        Assert.False(mgr.TryAcquireAll(2, new[] { "bench-1" }));
        Assert.Equal(1, mgr.HolderOf("bench-1")); // unchanged
    }

    // ENG-012: all-or-nothing - a partial conflict must not leave the requesting build holding
    // the resources that WERE free.
    [Fact]
    public void TryAcquireAll_PartialConflict_AcquiresNothing()
    {
        var mgr = new ResourceLockManager();
        mgr.TryAcquireAll(1, new[] { "bench-2" }); // bench-1 free, bench-2 held by build 1

        var acquired = mgr.TryAcquireAll(2, new[] { "bench-1", "bench-2" });

        Assert.False(acquired);
        Assert.Null(mgr.HolderOf("bench-1")); // never touched
        Assert.Equal(1, mgr.HolderOf("bench-2")); // unchanged
    }

    [Fact]
    public void ReleaseAll_FreesOnlyThatBuildsResources()
    {
        var mgr = new ResourceLockManager();
        mgr.TryAcquireAll(1, new[] { "bench-1" });
        mgr.TryAcquireAll(2, new[] { "bench-2" });

        mgr.ReleaseAll(1);

        Assert.Null(mgr.HolderOf("bench-1"));
        Assert.Equal(2, mgr.HolderOf("bench-2"));
    }

    [Fact]
    public void ReleaseAll_BuildThatNeverHeldAnything_IsANoOp()
    {
        var mgr = new ResourceLockManager();
        mgr.ReleaseAll(999);
        Assert.Empty(mgr.Snapshot());
    }

    [Fact]
    public void ForceRelease_FreesResourceAndReturnsPreviousHolder()
    {
        var mgr = new ResourceLockManager();
        mgr.TryAcquireAll(1, new[] { "bench-1" });

        var previousHolder = mgr.ForceRelease("bench-1");

        Assert.Equal(1, previousHolder);
        Assert.Null(mgr.HolderOf("bench-1"));
    }

    [Fact]
    public void ForceRelease_UnheldResource_ReturnsNull()
    {
        var mgr = new ResourceLockManager();
        Assert.Null(mgr.ForceRelease("bench-1"));
    }

    [Fact]
    public void TryAcquireAll_SameBuildReacquiring_Succeeds()
    {
        // Re-checking a build that already holds the resource (e.g. a re-dispatch tick before it's
        // marked Running) must not be treated as a conflict against itself.
        var mgr = new ResourceLockManager();
        mgr.TryAcquireAll(1, new[] { "bench-1" });

        Assert.True(mgr.TryAcquireAll(1, new[] { "bench-1" }));
    }
}
