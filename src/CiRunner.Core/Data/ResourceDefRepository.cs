using CiRunner.Core.Models;
using Microsoft.Data.Sqlite;

namespace CiRunner.Core.Data;

/// <summary>Free-text descriptions for resource names (spec §5 F3a/F6). Purely metadata - the
/// lock/wait state itself is runtime-only and owned by the dispatcher (F3a).</summary>
public sealed class ResourceDefRepository
{
    private readonly CiDatabase _db;

    public ResourceDefRepository(CiDatabase db)
    {
        _db = db;
    }

    public List<ResourceDefRecord> ListAll()
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM resource_defs ORDER BY name";
        using var reader = cmd.ExecuteReader();
        var result = new List<ResourceDefRecord>();
        while (reader.Read())
        {
            result.Add(Map(reader));
        }
        return result;
    }

    public ResourceDefRecord Upsert(string name, string? description, string updatedBy)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO resource_defs (name, description, updated_at, updated_by)
            VALUES ($name, $description, $updatedAt, $updatedBy)
            ON CONFLICT(name) DO UPDATE SET description = $description, updated_at = $updatedAt, updated_by = $updatedBy
            """;
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$description", (object?)description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$updatedAt", DateTimeOffset.Now.ToString("o"));
        cmd.Parameters.AddWithValue("$updatedBy", updatedBy);
        cmd.ExecuteNonQuery();

        using var find = conn.CreateCommand();
        find.CommandText = "SELECT * FROM resource_defs WHERE name = $name";
        find.Parameters.AddWithValue("$name", name);
        using var reader = find.ExecuteReader();
        reader.Read();
        return Map(reader);
    }

    public bool Delete(string name)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM resource_defs WHERE name = $name";
        cmd.Parameters.AddWithValue("$name", name);
        return cmd.ExecuteNonQuery() > 0;
    }

    private static ResourceDefRecord Map(SqliteDataReader r) => new()
    {
        Name = r.GetString(r.GetOrdinal("name")),
        Description = r.IsDBNull(r.GetOrdinal("description")) ? null : r.GetString(r.GetOrdinal("description")),
        UpdatedAt = r.GetString(r.GetOrdinal("updated_at")),
        UpdatedBy = r.IsDBNull(r.GetOrdinal("updated_by")) ? null : r.GetString(r.GetOrdinal("updated_by")),
    };
}
