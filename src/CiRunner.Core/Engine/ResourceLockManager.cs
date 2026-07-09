namespace CiRunner.Core.Engine;

/// <summary>
/// In-memory named-resource mutual exclusion (spec §5 F3a). Deliberately process-local, not
/// persisted: "プロセス内管理のためランナー再起動でも残留しない" - a restart always starts with
/// every resource free rather than risking a stale lock nobody can ever release.
/// </summary>
public sealed class ResourceLockManager
{
    private readonly object _lock = new();
    private readonly Dictionary<string, long> _heldBy = new();

    /// <summary>
    /// All-or-nothing acquire (spec §5 F3a "全取得か待機か"): either every requested resource is free
    /// (or already held by this same build) and all are atomically marked held, or none are touched.
    /// This is what makes deadlock structurally impossible - a build never holds a partial set.
    /// </summary>
    public bool TryAcquireAll(long buildId, IReadOnlyCollection<string> resources)
    {
        if (resources.Count == 0)
        {
            return true;
        }

        lock (_lock)
        {
            foreach (var name in resources)
            {
                if (_heldBy.TryGetValue(name, out var holder) && holder != buildId)
                {
                    return false;
                }
            }
            foreach (var name in resources)
            {
                _heldBy[name] = buildId;
            }
            return true;
        }
    }

    /// <summary>Releases everything held by this build (build finished/aborted, or was never running).</summary>
    public void ReleaseAll(long buildId)
    {
        lock (_lock)
        {
            var keys = _heldBy.Where(kv => kv.Value == buildId).Select(kv => kv.Key).ToList();
            foreach (var key in keys)
            {
                _heldBy.Remove(key);
            }
        }
    }

    /// <summary>Admin force-release for a stuck resource (spec §5 F3a "admin は設定画面から強制解放可能"). Returns the build id that was holding it, if any.</summary>
    public long? ForceRelease(string resource)
    {
        lock (_lock)
        {
            if (_heldBy.TryGetValue(resource, out var holder))
            {
                _heldBy.Remove(resource);
                return holder;
            }
            return null;
        }
    }

    public long? HolderOf(string resource)
    {
        lock (_lock)
        {
            return _heldBy.TryGetValue(resource, out var holder) ? holder : null;
        }
    }

    public Dictionary<string, long> Snapshot()
    {
        lock (_lock)
        {
            return new Dictionary<string, long>(_heldBy);
        }
    }
}
