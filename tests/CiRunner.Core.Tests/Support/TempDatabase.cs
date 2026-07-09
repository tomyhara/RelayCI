using CiRunner.Core.Data;

namespace CiRunner.Core.Tests.Support;

/// <summary>A migrated, per-test SQLite database backed by a temp file, deleted on dispose.</summary>
public sealed class TempDatabase : IDisposable
{
    public CiDatabase Db { get; }
    private readonly string _path;

    public TempDatabase()
    {
        _path = Path.Combine(Path.GetTempPath(), $"ci-test-{Guid.NewGuid()}.db");
        Db = new CiDatabase(_path);
        Db.Migrate();
    }

    public void Dispose()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var f = _path + suffix;
            if (File.Exists(f))
            {
                try { File.Delete(f); } catch { /* best-effort cleanup */ }
            }
        }
    }
}
