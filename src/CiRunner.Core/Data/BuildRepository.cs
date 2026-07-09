using CiRunner.Core.Models;
using Microsoft.Data.Sqlite;

namespace CiRunner.Core.Data;

public sealed class BuildRepository
{
    private readonly CiDatabase _db;
    private readonly object _numberLock = new();

    public BuildRepository(CiDatabase db)
    {
        _db = db;
    }

    /// <summary>Allocates the next build number for the job and inserts a Queued build. Serialized to avoid duplicate numbers.</summary>
    public BuildRecord CreateQueued(long jobId, string trigger, string parametersJson, string? dedupKey)
    {
        lock (_numberLock)
        {
            using var conn = _db.OpenConnection();
            using var tx = conn.BeginTransaction();

            int nextNumber;
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT COALESCE(MAX(number), 0) + 1 FROM builds WHERE job_id = $jobId";
                cmd.Parameters.AddWithValue("$jobId", jobId);
                nextNumber = Convert.ToInt32(cmd.ExecuteScalar());
            }

            var queuedAt = DateTimeOffset.Now.ToString("o");
            long buildId;
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO builds (job_id, number, status, trigger, parameters, dedup_key, queued_at)
                    VALUES ($jobId, $number, $status, $trigger, $parameters, $dedupKey, $queuedAt);
                    SELECT last_insert_rowid();
                    """;
                cmd.Parameters.AddWithValue("$jobId", jobId);
                cmd.Parameters.AddWithValue("$number", nextNumber);
                cmd.Parameters.AddWithValue("$status", BuildStatus.Queued);
                cmd.Parameters.AddWithValue("$trigger", trigger);
                cmd.Parameters.AddWithValue("$parameters", parametersJson);
                cmd.Parameters.AddWithValue("$dedupKey", (object?)dedupKey ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$queuedAt", queuedAt);
                buildId = (long)cmd.ExecuteScalar()!;
            }

            tx.Commit();
            return FindById(buildId)!;
        }
    }

    public BuildRecord? FindById(long id)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM builds WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public List<BuildRecord> ListByJob(long jobId, int limit = 50)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM builds WHERE job_id = $jobId ORDER BY number DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$jobId", jobId);
        cmd.Parameters.AddWithValue("$limit", limit);
        using var reader = cmd.ExecuteReader();
        var result = new List<BuildRecord>();
        while (reader.Read())
        {
            result.Add(Map(reader));
        }
        return result;
    }

    public int CountByStatus(string status)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM builds WHERE status = $status";
        cmd.Parameters.AddWithValue("$status", status);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public List<BuildRecord> ListQueued()
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM builds WHERE status = $status ORDER BY queued_at";
        cmd.Parameters.AddWithValue("$status", BuildStatus.Queued);
        using var reader = cmd.ExecuteReader();
        var result = new List<BuildRecord>();
        while (reader.Read())
        {
            result.Add(Map(reader));
        }
        return result;
    }

    public BuildRecord? FindLatestByJob(long jobId)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM builds WHERE job_id = $jobId ORDER BY number DESC LIMIT 1";
        cmd.Parameters.AddWithValue("$jobId", jobId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public void UpdateStatus(long buildId, string status, string? startedAt = null, string? finishedAt = null)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE builds SET status = $status,
                started_at = COALESCE($startedAt, started_at),
                finished_at = COALESCE($finishedAt, finished_at)
            WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$startedAt", (object?)startedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$finishedAt", (object?)finishedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", buildId);
        cmd.ExecuteNonQuery();
    }

    public void SetNote(long buildId, string note)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE builds SET note = $note WHERE id = $id";
        cmd.Parameters.AddWithValue("$note", note);
        cmd.Parameters.AddWithValue("$id", buildId);
        cmd.ExecuteNonQuery();
    }

    public void SetCommitInfo(long buildId, string? sha, string? branch)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE builds SET commit_sha = $sha, branch = $branch WHERE id = $id";
        cmd.Parameters.AddWithValue("$sha", (object?)sha ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$branch", (object?)branch ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", buildId);
        cmd.ExecuteNonQuery();
    }

    public long UpsertStepStart(long buildId, int seq, string name, string? post)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO build_steps (build_id, seq, name, status, post, started_at)
            VALUES ($buildId, $seq, $name, $status, $post, $startedAt);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$buildId", buildId);
        cmd.Parameters.AddWithValue("$seq", seq);
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$status", StepStatus.Running);
        cmd.Parameters.AddWithValue("$post", (object?)post ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$startedAt", DateTimeOffset.Now.ToString("o"));
        return (long)cmd.ExecuteScalar()!;
    }

    public void UpdateStepEnd(long buildId, int seq, string status, string? error)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE build_steps SET status = $status, error = $error, finished_at = $finishedAt
            WHERE build_id = $buildId AND seq = $seq
            """;
        cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$finishedAt", DateTimeOffset.Now.ToString("o"));
        cmd.Parameters.AddWithValue("$buildId", buildId);
        cmd.Parameters.AddWithValue("$seq", seq);
        cmd.ExecuteNonQuery();
    }

    public void UpdateStepLogOffsets(long buildId, int seq, long? offsetStart, long? offsetEnd)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE build_steps SET
                log_offset_start = COALESCE($offsetStart, log_offset_start),
                log_offset_end = COALESCE($offsetEnd, log_offset_end)
            WHERE build_id = $buildId AND seq = $seq
            """;
        cmd.Parameters.AddWithValue("$offsetStart", (object?)offsetStart ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$offsetEnd", (object?)offsetEnd ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$buildId", buildId);
        cmd.Parameters.AddWithValue("$seq", seq);
        cmd.ExecuteNonQuery();
    }

    public List<BuildStepRecord> ListSteps(long buildId)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM build_steps WHERE build_id = $buildId ORDER BY seq";
        cmd.Parameters.AddWithValue("$buildId", buildId);
        using var reader = cmd.ExecuteReader();
        var result = new List<BuildStepRecord>();
        while (reader.Read())
        {
            result.Add(new BuildStepRecord
            {
                Id = reader.GetInt64(reader.GetOrdinal("id")),
                BuildId = reader.GetInt64(reader.GetOrdinal("build_id")),
                Seq = reader.GetInt32(reader.GetOrdinal("seq")),
                Name = reader.GetString(reader.GetOrdinal("name")),
                Status = reader.GetString(reader.GetOrdinal("status")),
                Post = reader.IsDBNull(reader.GetOrdinal("post")) ? null : reader.GetString(reader.GetOrdinal("post")),
                Error = reader.IsDBNull(reader.GetOrdinal("error")) ? null : reader.GetString(reader.GetOrdinal("error")),
                StartedAt = reader.IsDBNull(reader.GetOrdinal("started_at")) ? null : reader.GetString(reader.GetOrdinal("started_at")),
                FinishedAt = reader.IsDBNull(reader.GetOrdinal("finished_at")) ? null : reader.GetString(reader.GetOrdinal("finished_at")),
                LogOffsetStart = reader.IsDBNull(reader.GetOrdinal("log_offset_start")) ? null : reader.GetInt64(reader.GetOrdinal("log_offset_start")),
                LogOffsetEnd = reader.IsDBNull(reader.GetOrdinal("log_offset_end")) ? null : reader.GetInt64(reader.GetOrdinal("log_offset_end")),
            });
        }
        return result;
    }

    private static BuildRecord Map(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(r.GetOrdinal("id")),
        JobId = r.GetInt64(r.GetOrdinal("job_id")),
        Number = r.GetInt32(r.GetOrdinal("number")),
        Status = r.GetString(r.GetOrdinal("status")),
        Trigger = r.GetString(r.GetOrdinal("trigger")),
        Parameters = r.GetString(r.GetOrdinal("parameters")),
        DedupKey = r.IsDBNull(r.GetOrdinal("dedup_key")) ? null : r.GetString(r.GetOrdinal("dedup_key")),
        CommitSha = r.IsDBNull(r.GetOrdinal("commit_sha")) ? null : r.GetString(r.GetOrdinal("commit_sha")),
        Branch = r.IsDBNull(r.GetOrdinal("branch")) ? null : r.GetString(r.GetOrdinal("branch")),
        PrNumber = r.IsDBNull(r.GetOrdinal("pr_number")) ? null : r.GetInt32(r.GetOrdinal("pr_number")),
        Note = r.IsDBNull(r.GetOrdinal("note")) ? null : r.GetString(r.GetOrdinal("note")),
        QueuedAt = r.GetString(r.GetOrdinal("queued_at")),
        StartedAt = r.IsDBNull(r.GetOrdinal("started_at")) ? null : r.GetString(r.GetOrdinal("started_at")),
        FinishedAt = r.IsDBNull(r.GetOrdinal("finished_at")) ? null : r.GetString(r.GetOrdinal("finished_at")),
    };
}
