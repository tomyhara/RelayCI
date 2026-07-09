using CiRunner.Core.Models;
using Microsoft.Data.Sqlite;

namespace CiRunner.Core.Data;

/// <summary>Audit trail (spec §5 F6 "すべての設定変更は監査ログに記録する"): who/when/what/before/after.</summary>
public sealed class AuditLogRepository
{
    private readonly CiDatabase _db;

    public AuditLogRepository(CiDatabase db)
    {
        _db = db;
    }

    public void Record(string username, string action, string? target, string? beforeJson, string? afterJson)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO audit_log (at, username, action, target, before_json, after_json)
            VALUES ($at, $username, $action, $target, $before, $after)
            """;
        cmd.Parameters.AddWithValue("$at", DateTimeOffset.Now.ToString("o"));
        cmd.Parameters.AddWithValue("$username", username);
        cmd.Parameters.AddWithValue("$action", action);
        cmd.Parameters.AddWithValue("$target", (object?)target ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$before", (object?)beforeJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$after", (object?)afterJson ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public List<AuditLogEntry> ListRecent(int limit = 200)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM audit_log ORDER BY id DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$limit", limit);
        using var reader = cmd.ExecuteReader();
        var result = new List<AuditLogEntry>();
        while (reader.Read())
        {
            result.Add(new AuditLogEntry
            {
                Id = reader.GetInt64(reader.GetOrdinal("id")),
                At = reader.GetString(reader.GetOrdinal("at")),
                Username = reader.GetString(reader.GetOrdinal("username")),
                Action = reader.GetString(reader.GetOrdinal("action")),
                Target = reader.IsDBNull(reader.GetOrdinal("target")) ? null : reader.GetString(reader.GetOrdinal("target")),
                BeforeJson = reader.IsDBNull(reader.GetOrdinal("before_json")) ? null : reader.GetString(reader.GetOrdinal("before_json")),
                AfterJson = reader.IsDBNull(reader.GetOrdinal("after_json")) ? null : reader.GetString(reader.GetOrdinal("after_json")),
            });
        }
        return result;
    }
}
