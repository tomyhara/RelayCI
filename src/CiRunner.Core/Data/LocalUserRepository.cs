using CiRunner.Core.Models;
using Microsoft.Data.Sqlite;

namespace CiRunner.Core.Data;

/// <summary>`local_users` table (spec §7/§9, auth.mode = "local"). Bootstrapping the first row happens
/// via the `user add` CLI subcommand (CiRunner.Host), not this repository directly, but the repository
/// itself has no opinion on caller (CLI vs admin API vs tests).</summary>
public sealed class LocalUserRepository
{
    private readonly CiDatabase _db;

    public LocalUserRepository(CiDatabase db)
    {
        _db = db;
    }

    public LocalUserRecord? FindByUsername(string username)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM local_users WHERE username = $username";
        cmd.Parameters.AddWithValue("$username", username);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public List<LocalUserRecord> ListAll()
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM local_users ORDER BY username";
        using var reader = cmd.ExecuteReader();
        var result = new List<LocalUserRecord>();
        while (reader.Read())
        {
            result.Add(Map(reader));
        }
        return result;
    }

    /// <summary>Creates a new user. Throws if the username already exists - callers (CLI, admin API)
    /// are expected to check first if they want a friendlier error message.</summary>
    public LocalUserRecord Add(string username, string passwordHash, string? displayName)
    {
        var now = DateTimeOffset.Now.ToString("o");
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO local_users (username, password_hash, display_name, enabled, created_at, updated_at)
            VALUES ($username, $hash, $displayName, 1, $now, $now)
            """;
        cmd.Parameters.AddWithValue("$username", username);
        cmd.Parameters.AddWithValue("$hash", passwordHash);
        cmd.Parameters.AddWithValue("$displayName", (object?)displayName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.ExecuteNonQuery();

        return FindByUsername(username)!;
    }

    public bool UpdatePassword(string username, string passwordHash)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE local_users SET password_hash = $hash, updated_at = $now WHERE username = $username";
        cmd.Parameters.AddWithValue("$username", username);
        cmd.Parameters.AddWithValue("$hash", passwordHash);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("o"));
        return cmd.ExecuteNonQuery() > 0;
    }

    public bool SetEnabled(string username, bool enabled)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE local_users SET enabled = $enabled, updated_at = $now WHERE username = $username";
        cmd.Parameters.AddWithValue("$username", username);
        cmd.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("o"));
        return cmd.ExecuteNonQuery() > 0;
    }

    public bool Delete(string username)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM local_users WHERE username = $username";
        cmd.Parameters.AddWithValue("$username", username);
        return cmd.ExecuteNonQuery() > 0;
    }

    private static LocalUserRecord Map(SqliteDataReader r) => new()
    {
        Username = r.GetString(r.GetOrdinal("username")),
        PasswordHash = r.GetString(r.GetOrdinal("password_hash")),
        DisplayName = r.IsDBNull(r.GetOrdinal("display_name")) ? null : r.GetString(r.GetOrdinal("display_name")),
        Enabled = r.GetInt64(r.GetOrdinal("enabled")) != 0,
        CreatedAt = r.GetString(r.GetOrdinal("created_at")),
        UpdatedAt = r.GetString(r.GetOrdinal("updated_at")),
    };
}
