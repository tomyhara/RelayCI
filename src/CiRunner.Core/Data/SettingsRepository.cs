namespace CiRunner.Core.Data;

/// <summary>Key-value system settings (spec §7 `settings` table). Full admin UI (F6) is a later milestone.</summary>
public sealed class SettingsRepository
{
    private readonly CiDatabase _db;

    public SettingsRepository(CiDatabase db)
    {
        _db = db;
    }

    public int GetInt(string key, int defaultValue)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key = $key";
        cmd.Parameters.AddWithValue("$key", key);
        var result = cmd.ExecuteScalar();
        return result is string s && int.TryParse(s, out var v) ? v : defaultValue;
    }

    public void Set(string key, string value, string? updatedBy = null)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO settings (key, value, updated_at, updated_by) VALUES ($key, $value, $now, $by)
            ON CONFLICT(key) DO UPDATE SET value = $value, updated_at = $now, updated_by = $by
            """;
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$value", value);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("o"));
        cmd.Parameters.AddWithValue("$by", (object?)updatedBy ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }
}
