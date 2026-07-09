using CiRunner.Core.Models;
using Microsoft.Data.Sqlite;

namespace CiRunner.Core.Data;

public sealed class JobRepository
{
    private readonly CiDatabase _db;

    public JobRepository(CiDatabase db)
    {
        _db = db;
    }

    public JobRecord? FindByName(string name)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM jobs WHERE name = $name AND deleted = 0";
        cmd.Parameters.AddWithValue("$name", name);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public JobRecord? FindById(long id)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM jobs WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public List<JobRecord> ListEnabled()
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM jobs WHERE deleted = 0 ORDER BY name";
        using var reader = cmd.ExecuteReader();
        var result = new List<JobRecord>();
        while (reader.Read())
        {
            result.Add(Map(reader));
        }
        return result;
    }

    public JobRecord UpsertServerJob(string name)
    {
        var existing = FindByName(name);
        if (existing is not null)
        {
            return existing;
        }

        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO jobs (name, pipeline_source, pipeline_path, created_at)
            VALUES ($name, 'server', 'pipeline.cipipe', $createdAt)
            """;
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$createdAt", DateTimeOffset.Now.ToString("o"));
        cmd.ExecuteNonQuery();

        return FindByName(name)!;
    }

    /// <summary>
    /// Registers or updates a job from an optional jobs/&lt;name&gt;/job.json (stand-in for the F6
    /// job-management admin UI): parameters, cron/polling triggers, resources, queue policy etc.
    /// Re-applied on every scan, so job.json is the source of truth for anything it sets.
    /// </summary>
    public JobRecord UpsertConfiguredJob(JobConfigInput cfg)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO jobs (
                name, repo_url, workspace_path, pipeline_source, pipeline_path, parameters,
                cron_schedules, polling_branches, resources, queue_policy, timeout_minutes,
                retention, shell_path, enabled, created_at)
            VALUES (
                $name, $repoUrl, $workspacePath, $pipelineSource, $pipelinePath, $parameters,
                $cronSchedules, $pollingBranches, $resources, $queuePolicy, $timeoutMinutes,
                $retention, $shellPath, $enabled, $createdAt)
            ON CONFLICT(name) DO UPDATE SET
                repo_url = $repoUrl, workspace_path = $workspacePath, pipeline_source = $pipelineSource,
                pipeline_path = $pipelinePath, parameters = $parameters, cron_schedules = $cronSchedules,
                polling_branches = $pollingBranches, resources = $resources, queue_policy = $queuePolicy,
                timeout_minutes = $timeoutMinutes, retention = $retention, shell_path = $shellPath,
                enabled = $enabled
            """;
        cmd.Parameters.AddWithValue("$name", cfg.Name);
        cmd.Parameters.AddWithValue("$repoUrl", (object?)cfg.RepoUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$workspacePath", (object?)cfg.WorkspacePath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$pipelineSource", cfg.PipelineSource);
        cmd.Parameters.AddWithValue("$pipelinePath", cfg.PipelinePath);
        cmd.Parameters.AddWithValue("$parameters", cfg.ParametersJson);
        cmd.Parameters.AddWithValue("$cronSchedules", cfg.CronSchedulesJson);
        cmd.Parameters.AddWithValue("$pollingBranches", (object?)cfg.PollingBranchesJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$resources", cfg.ResourcesJson);
        cmd.Parameters.AddWithValue("$queuePolicy", cfg.QueuePolicy);
        cmd.Parameters.AddWithValue("$timeoutMinutes", (object?)cfg.TimeoutMinutes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$retention", (object?)cfg.Retention ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$shellPath", (object?)cfg.ShellPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$enabled", cfg.Enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$createdAt", DateTimeOffset.Now.ToString("o"));
        cmd.ExecuteNonQuery();

        return FindByName(cfg.Name)!;
    }

    private static JobRecord Map(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(r.GetOrdinal("id")),
        Name = r.GetString(r.GetOrdinal("name")),
        RepoUrl = r.IsDBNull(r.GetOrdinal("repo_url")) ? null : r.GetString(r.GetOrdinal("repo_url")),
        WorkspacePath = r.IsDBNull(r.GetOrdinal("workspace_path")) ? null : r.GetString(r.GetOrdinal("workspace_path")),
        PipelineSource = r.GetString(r.GetOrdinal("pipeline_source")),
        PipelinePath = r.IsDBNull(r.GetOrdinal("pipeline_path")) ? ".ci/pipeline.cipipe" : r.GetString(r.GetOrdinal("pipeline_path")),
        Parameters = r.GetString(r.GetOrdinal("parameters")),
        CronSchedules = r.GetString(r.GetOrdinal("cron_schedules")),
        PollingBranches = r.IsDBNull(r.GetOrdinal("polling_branches")) ? null : r.GetString(r.GetOrdinal("polling_branches")),
        Resources = r.GetString(r.GetOrdinal("resources")),
        QueuePolicy = r.GetString(r.GetOrdinal("queue_policy")),
        TimeoutMinutes = r.IsDBNull(r.GetOrdinal("timeout_minutes")) ? null : r.GetInt32(r.GetOrdinal("timeout_minutes")),
        Retention = r.IsDBNull(r.GetOrdinal("retention")) ? null : r.GetInt32(r.GetOrdinal("retention")),
        ShellPath = r.IsDBNull(r.GetOrdinal("shell_path")) ? null : r.GetString(r.GetOrdinal("shell_path")),
        Enabled = r.GetInt64(r.GetOrdinal("enabled")) != 0,
        Deleted = r.GetInt64(r.GetOrdinal("deleted")) != 0,
        CreatedAt = r.GetString(r.GetOrdinal("created_at")),
    };
}
