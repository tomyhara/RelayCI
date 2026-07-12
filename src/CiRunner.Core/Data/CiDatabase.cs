using Microsoft.Data.Sqlite;

namespace CiRunner.Core.Data;

/// <summary>
/// SQLite (WAL mode) connection factory and schema initializer.
/// Schema per ci-runner-spec.md §7. Migrations are applied at startup (spec §10 "DB スキーマはマイグレーション内蔵").
/// </summary>
public sealed class CiDatabase
{
    private readonly string _connectionString;

    public CiDatabase(string dbPath)
    {
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Cache = SqliteCacheMode.Shared,
        }.ToString();
    }

    public SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;";
            pragma.ExecuteNonQuery();
        }
        return conn;
    }

    public void Migrate()
    {
        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = Schema;
            cmd.ExecuteNonQuery();
        }
        AddColumnIfMissing(conn, tx, "hooks", "deleted", "INTEGER NOT NULL DEFAULT 0");
        tx.Commit();
    }

    /// <summary>
    /// `CREATE TABLE IF NOT EXISTS` doesn't add columns to a table that already exists from an
    /// earlier version, so new columns on existing tables need this instead (SQLite has no
    /// `ADD COLUMN IF NOT EXISTS`).
    /// </summary>
    private static void AddColumnIfMissing(SqliteConnection conn, SqliteTransaction tx, string table, string column, string columnDefSql)
    {
        using (var check = conn.CreateCommand())
        {
            check.Transaction = tx;
            check.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $column";
            check.Parameters.AddWithValue("$column", column);
            if (Convert.ToInt64(check.ExecuteScalar()) > 0)
            {
                return;
            }
        }

        using var alter = conn.CreateCommand();
        alter.Transaction = tx;
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {columnDefSql}";
        alter.ExecuteNonQuery();
    }

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS jobs (
            id INTEGER PRIMARY KEY,
            name TEXT UNIQUE NOT NULL,
            repo_url TEXT,
            workspace_path TEXT,
            pipeline_source TEXT NOT NULL DEFAULT 'server',
            pipeline_path TEXT DEFAULT '.ci/pipeline.cipipe',
            parameters TEXT NOT NULL DEFAULT '[]',
            cron_schedules TEXT NOT NULL DEFAULT '[]',
            polling_branches TEXT,
            resources TEXT NOT NULL DEFAULT '[]',
            queue_policy TEXT NOT NULL DEFAULT 'replace',
            timeout_minutes INTEGER,
            retention INTEGER,
            shell_path TEXT,
            enabled INTEGER NOT NULL DEFAULT 1,
            deleted INTEGER NOT NULL DEFAULT 0,
            created_at TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS hooks (
            id INTEGER PRIMARY KEY,
            name TEXT UNIQUE NOT NULL,
            secret TEXT,
            handler_path TEXT NOT NULL,
            timeout_sec INTEGER NOT NULL DEFAULT 60,
            enabled INTEGER NOT NULL DEFAULT 1,
            deleted INTEGER NOT NULL DEFAULT 0,
            created_at TEXT NOT NULL
        );

        -- Free-text descriptions for resource names declared on jobs.resources (spec §5 F3a/F6:
        -- "リソースは事前定義不要の単なる文字列(設定画面で一覧・説明を管理可能)"). The lock state
        -- itself (held/waiting) is runtime-only (F3a), not persisted here.
        CREATE TABLE IF NOT EXISTS resource_defs (
            name TEXT PRIMARY KEY,
            description TEXT,
            updated_at TEXT NOT NULL,
            updated_by TEXT
        );

        CREATE TABLE IF NOT EXISTS hook_runs (
            id INTEGER PRIMARY KEY,
            hook_id INTEGER NOT NULL REFERENCES hooks(id),
            delivery_id TEXT,
            event TEXT,
            received_at TEXT NOT NULL,
            status TEXT NOT NULL,
            triggered_builds TEXT NOT NULL DEFAULT '[]',
            payload_path TEXT,
            log_path TEXT
        );

        CREATE TABLE IF NOT EXISTS settings (
            key TEXT PRIMARY KEY,
            value TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            updated_by TEXT
        );

        CREATE TABLE IF NOT EXISTS audit_log (
            id INTEGER PRIMARY KEY,
            at TEXT NOT NULL,
            username TEXT NOT NULL,
            action TEXT NOT NULL,
            target TEXT,
            before_json TEXT,
            after_json TEXT
        );

        CREATE TABLE IF NOT EXISTS builds (
            id INTEGER PRIMARY KEY,
            job_id INTEGER NOT NULL REFERENCES jobs(id),
            number INTEGER NOT NULL,
            status TEXT NOT NULL,
            trigger TEXT NOT NULL,
            parameters TEXT NOT NULL DEFAULT '{}',
            dedup_key TEXT,
            commit_sha TEXT,
            branch TEXT,
            pr_number INTEGER,
            note TEXT,
            queued_at TEXT NOT NULL,
            started_at TEXT,
            finished_at TEXT,
            UNIQUE(job_id, number)
        );

        CREATE TABLE IF NOT EXISTS build_steps (
            id INTEGER PRIMARY KEY,
            build_id INTEGER NOT NULL REFERENCES builds(id),
            seq INTEGER NOT NULL,
            name TEXT NOT NULL,
            status TEXT NOT NULL,
            post TEXT,
            error TEXT,
            started_at TEXT,
            finished_at TEXT,
            log_offset_start INTEGER,
            log_offset_end INTEGER
        );

        CREATE TABLE IF NOT EXISTS test_results (
            id INTEGER PRIMARY KEY,
            build_id INTEGER NOT NULL REFERENCES builds(id),
            suite TEXT, name TEXT NOT NULL,
            status TEXT NOT NULL,
            duration_ms INTEGER,
            message TEXT
        );

        CREATE TABLE IF NOT EXISTS user_roles (
            username TEXT PRIMARY KEY,
            role TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            updated_by TEXT
        );

        -- auth.mode = "local" only (spec §9): local account store. password_hash is PBKDF2-SHA256,
        -- encoded as `<iterations>.<salt/base64>.<hash/base64>` (Pbkdf2PasswordHasher).
        CREATE TABLE IF NOT EXISTS local_users (
            username TEXT PRIMARY KEY,
            password_hash TEXT NOT NULL,
            display_name TEXT,
            enabled INTEGER NOT NULL DEFAULT 1,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS artifacts (
            id INTEGER PRIMARY KEY,
            build_id INTEGER NOT NULL REFERENCES builds(id),
            path TEXT NOT NULL,
            size INTEGER
        );

        -- Not in the spec's §7 listing but required by §5 F6 / §9 ("APIトークン" admin screen):
        -- admin-issued Bearer tokens for scripts. Only a salted hash of the token is stored -
        -- the raw value is shown once at issuance and is not recoverable afterward.
        CREATE TABLE IF NOT EXISTS api_tokens (
            id INTEGER PRIMARY KEY,
            name TEXT NOT NULL,
            token_hash TEXT UNIQUE NOT NULL,
            role TEXT NOT NULL,
            created_at TEXT NOT NULL,
            created_by TEXT NOT NULL,
            last_used_at TEXT,
            revoked_at TEXT
        );

        CREATE INDEX IF NOT EXISTS idx_builds_job ON builds(job_id, number);
        CREATE INDEX IF NOT EXISTS idx_build_steps_build ON build_steps(build_id, seq);
        CREATE INDEX IF NOT EXISTS idx_test_results_build ON test_results(build_id);
        CREATE INDEX IF NOT EXISTS idx_hook_runs_hook ON hook_runs(hook_id, received_at);
        CREATE INDEX IF NOT EXISTS idx_hook_runs_delivery ON hook_runs(hook_id, delivery_id);
        """;
}
