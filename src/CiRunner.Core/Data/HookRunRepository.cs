using CiRunner.Core.Models;
using Microsoft.Data.Sqlite;

namespace CiRunner.Core.Data;

public sealed class HookRunRepository
{
    private readonly CiDatabase _db;

    public HookRunRepository(CiDatabase db)
    {
        _db = db;
    }

    /// <summary>
    /// Delivery-ID idempotency check (spec §5 F1): any prior run for this hook+delivery counts as a
    /// duplicate. Indexed on (hook_id, delivery_id) so this stays cheap regardless of table size;
    /// the spec's "直近 1000 件" framing is about retention/display, not about bounding this check.
    /// </summary>
    public bool IsDuplicateDelivery(long hookId, string deliveryId)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM hook_runs WHERE hook_id = $hookId AND delivery_id = $deliveryId LIMIT 1";
        cmd.Parameters.AddWithValue("$hookId", hookId);
        cmd.Parameters.AddWithValue("$deliveryId", deliveryId);
        return cmd.ExecuteScalar() is not null;
    }

    public long CreateRunning(long hookId, string? deliveryId, string? eventName, string? payloadPath)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO hook_runs (hook_id, delivery_id, event, received_at, status, payload_path)
            VALUES ($hookId, $deliveryId, $event, $receivedAt, $status, $payloadPath);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$hookId", hookId);
        cmd.Parameters.AddWithValue("$deliveryId", (object?)deliveryId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$event", (object?)eventName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$receivedAt", DateTimeOffset.Now.ToString("o"));
        cmd.Parameters.AddWithValue("$status", HookRunStatus.Running);
        cmd.Parameters.AddWithValue("$payloadPath", (object?)payloadPath ?? DBNull.Value);
        return (long)cmd.ExecuteScalar()!;
    }

    public void Complete(long runId, string status, string? logPath)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE hook_runs SET status = $status, log_path = $logPath WHERE id = $id";
        cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$logPath", (object?)logPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", runId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Appends a build id triggered by Start-CiJob during this hook run (read-modify-write on the JSON array column).</summary>
    public void AppendTriggeredBuild(long hookRunId, long buildId)
    {
        using var conn = _db.OpenConnection();
        using var tx = conn.BeginTransaction();

        string current;
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT triggered_builds FROM hook_runs WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", hookRunId);
            current = cmd.ExecuteScalar() as string ?? "[]";
        }

        var list = System.Text.Json.JsonSerializer.Deserialize<List<long>>(current) ?? new List<long>();
        list.Add(buildId);
        var json = System.Text.Json.JsonSerializer.Serialize(list);

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE hook_runs SET triggered_builds = $json WHERE id = $id";
            cmd.Parameters.AddWithValue("$json", json);
            cmd.Parameters.AddWithValue("$id", hookRunId);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public List<HookRunRecord> ListRecent(long hookId, int limit = 50)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM hook_runs WHERE hook_id = $hookId ORDER BY received_at DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$hookId", hookId);
        cmd.Parameters.AddWithValue("$limit", limit);
        using var reader = cmd.ExecuteReader();
        var result = new List<HookRunRecord>();
        while (reader.Read())
        {
            result.Add(Map(reader));
        }
        return result;
    }

    private static HookRunRecord Map(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(r.GetOrdinal("id")),
        HookId = r.GetInt64(r.GetOrdinal("hook_id")),
        DeliveryId = r.IsDBNull(r.GetOrdinal("delivery_id")) ? null : r.GetString(r.GetOrdinal("delivery_id")),
        Event = r.IsDBNull(r.GetOrdinal("event")) ? null : r.GetString(r.GetOrdinal("event")),
        ReceivedAt = r.GetString(r.GetOrdinal("received_at")),
        Status = r.GetString(r.GetOrdinal("status")),
        TriggeredBuilds = r.GetString(r.GetOrdinal("triggered_builds")),
        PayloadPath = r.IsDBNull(r.GetOrdinal("payload_path")) ? null : r.GetString(r.GetOrdinal("payload_path")),
        LogPath = r.IsDBNull(r.GetOrdinal("log_path")) ? null : r.GetString(r.GetOrdinal("log_path")),
    };
}
