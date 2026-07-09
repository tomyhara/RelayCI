using CiRunner.Core.Models;
using Microsoft.Data.Sqlite;

namespace CiRunner.Core.Data;

public sealed class ApiTokenRepository
{
    private readonly CiDatabase _db;

    public ApiTokenRepository(CiDatabase db)
    {
        _db = db;
    }

    public long Insert(string name, string tokenHash, string role, string createdBy)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO api_tokens (name, token_hash, role, created_at, created_by)
            VALUES ($name, $hash, $role, $now, $by);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$hash", tokenHash);
        cmd.Parameters.AddWithValue("$role", role);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("o"));
        cmd.Parameters.AddWithValue("$by", createdBy);
        return (long)cmd.ExecuteScalar()!;
    }

    /// <summary>Active (non-revoked) token matching this hash, or null. Also bumps last_used_at.</summary>
    public ApiTokenRecord? FindActiveByHashAndTouch(string tokenHash)
    {
        using var conn = _db.OpenConnection();
        ApiTokenRecord? record;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT * FROM api_tokens WHERE token_hash = $hash AND revoked_at IS NULL";
            cmd.Parameters.AddWithValue("$hash", tokenHash);
            using var reader = cmd.ExecuteReader();
            record = reader.Read() ? Map(reader) : null;
        }
        if (record is not null)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE api_tokens SET last_used_at = $now WHERE id = $id";
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("o"));
            cmd.Parameters.AddWithValue("$id", record.Id);
            cmd.ExecuteNonQuery();
        }
        return record;
    }

    public List<ApiTokenRecord> ListAll()
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM api_tokens ORDER BY created_at DESC";
        using var reader = cmd.ExecuteReader();
        var result = new List<ApiTokenRecord>();
        while (reader.Read())
        {
            result.Add(Map(reader));
        }
        return result;
    }

    public bool Revoke(long id)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE api_tokens SET revoked_at = $now WHERE id = $id AND revoked_at IS NULL";
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("o"));
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery() > 0;
    }

    private static ApiTokenRecord Map(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(r.GetOrdinal("id")),
        Name = r.GetString(r.GetOrdinal("name")),
        TokenHash = r.GetString(r.GetOrdinal("token_hash")),
        Role = r.GetString(r.GetOrdinal("role")),
        CreatedAt = r.GetString(r.GetOrdinal("created_at")),
        CreatedBy = r.GetString(r.GetOrdinal("created_by")),
        LastUsedAt = r.IsDBNull(r.GetOrdinal("last_used_at")) ? null : r.GetString(r.GetOrdinal("last_used_at")),
        RevokedAt = r.IsDBNull(r.GetOrdinal("revoked_at")) ? null : r.GetString(r.GetOrdinal("revoked_at")),
    };
}
