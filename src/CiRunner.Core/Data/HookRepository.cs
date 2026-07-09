using CiRunner.Core.Models;
using Microsoft.Data.Sqlite;

namespace CiRunner.Core.Data;

public sealed class HookRepository
{
    private readonly CiDatabase _db;

    public HookRepository(CiDatabase db)
    {
        _db = db;
    }

    public HookRecord? FindByName(string name)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM hooks WHERE name = $name AND deleted = 0";
        cmd.Parameters.AddWithValue("$name", name);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public List<HookRecord> ListEnabled()
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM hooks WHERE deleted = 0 ORDER BY name";
        using var reader = cmd.ExecuteReader();
        var result = new List<HookRecord>();
        while (reader.Read())
        {
            result.Add(Map(reader));
        }
        return result;
    }

    /// <summary>Logical delete (spec §5 F6), mirroring JobRepository.SoftDelete: hook_runs history survives.</summary>
    public bool SoftDelete(string name)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE hooks SET deleted = 1 WHERE name = $name AND deleted = 0";
        cmd.Parameters.AddWithValue("$name", name);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>Mirrors JobRepository.Undelete: reactivates a soft-deleted row for a deliberate admin re-create.</summary>
    public bool Undelete(string name)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE hooks SET deleted = 0 WHERE name = $name AND deleted = 1";
        cmd.Parameters.AddWithValue("$name", name);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>Registers or updates a server-discovered hook (stand-in for the F6 hook-management admin UI).</summary>
    public HookRecord UpsertDiscoveredHook(string name, string handlerPath, string? secret, int timeoutSec, bool enabled)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO hooks (name, secret, handler_path, timeout_sec, enabled, created_at)
            VALUES ($name, $secret, $handlerPath, $timeoutSec, $enabled, $createdAt)
            ON CONFLICT(name) DO UPDATE SET
                secret = $secret, handler_path = $handlerPath, timeout_sec = $timeoutSec, enabled = $enabled
            """;
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$secret", (object?)secret ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$handlerPath", handlerPath);
        cmd.Parameters.AddWithValue("$timeoutSec", timeoutSec);
        cmd.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$createdAt", DateTimeOffset.Now.ToString("o"));
        cmd.ExecuteNonQuery();

        return FindByName(name)!;
    }

    private static HookRecord Map(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(r.GetOrdinal("id")),
        Name = r.GetString(r.GetOrdinal("name")),
        Secret = r.IsDBNull(r.GetOrdinal("secret")) ? null : r.GetString(r.GetOrdinal("secret")),
        HandlerPath = r.GetString(r.GetOrdinal("handler_path")),
        TimeoutSec = r.GetInt32(r.GetOrdinal("timeout_sec")),
        Enabled = r.GetInt64(r.GetOrdinal("enabled")) != 0,
        Deleted = r.GetInt64(r.GetOrdinal("deleted")) != 0,
        CreatedAt = r.GetString(r.GetOrdinal("created_at")),
    };
}
