using CiRunner.Core.Models;
using Microsoft.Data.Sqlite;

namespace CiRunner.Core.Data;

public sealed class TestResultRepository
{
    private readonly CiDatabase _db;

    public TestResultRepository(CiDatabase db)
    {
        _db = db;
    }

    public void InsertMany(long buildId, IEnumerable<TestResultRecord> results)
    {
        using var conn = _db.OpenConnection();
        using var tx = conn.BeginTransaction();
        foreach (var r in results)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO test_results (build_id, suite, name, status, duration_ms, message)
                VALUES ($buildId, $suite, $name, $status, $durationMs, $message)
                """;
            cmd.Parameters.AddWithValue("$buildId", buildId);
            cmd.Parameters.AddWithValue("$suite", (object?)r.Suite ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$name", r.Name);
            cmd.Parameters.AddWithValue("$status", r.Status);
            cmd.Parameters.AddWithValue("$durationMs", (object?)r.DurationMs ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$message", (object?)r.Message ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public List<TestResultRecord> ListByBuild(long buildId)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM test_results WHERE build_id = $buildId ORDER BY id";
        cmd.Parameters.AddWithValue("$buildId", buildId);
        using var reader = cmd.ExecuteReader();
        var result = new List<TestResultRecord>();
        while (reader.Read())
        {
            result.Add(Map(reader));
        }
        return result;
    }

    /// <summary>True if any test for this build is failed/error (spec §5 F4 strict mode).</summary>
    public bool HasFailures(long buildId)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT 1 FROM test_results
            WHERE build_id = $buildId AND status IN ($failed, $error) LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$buildId", buildId);
        cmd.Parameters.AddWithValue("$failed", TestCaseStatus.Failed);
        cmd.Parameters.AddWithValue("$error", TestCaseStatus.Error);
        return cmd.ExecuteScalar() is not null;
    }

    private static TestResultRecord Map(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(r.GetOrdinal("id")),
        BuildId = r.GetInt64(r.GetOrdinal("build_id")),
        Suite = r.IsDBNull(r.GetOrdinal("suite")) ? null : r.GetString(r.GetOrdinal("suite")),
        Name = r.GetString(r.GetOrdinal("name")),
        Status = r.GetString(r.GetOrdinal("status")),
        DurationMs = r.IsDBNull(r.GetOrdinal("duration_ms")) ? null : r.GetInt64(r.GetOrdinal("duration_ms")),
        Message = r.IsDBNull(r.GetOrdinal("message")) ? null : r.GetString(r.GetOrdinal("message")),
    };
}
