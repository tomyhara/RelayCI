using CiRunner.Core.Models;
using Microsoft.Data.Sqlite;

namespace CiRunner.Core.Data;

public sealed class ArtifactRepository
{
    private readonly CiDatabase _db;

    public ArtifactRepository(CiDatabase db)
    {
        _db = db;
    }

    public void Insert(long buildId, string path, long? size)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO artifacts (build_id, path, size) VALUES ($buildId, $path, $size)";
        cmd.Parameters.AddWithValue("$buildId", buildId);
        cmd.Parameters.AddWithValue("$path", path);
        cmd.Parameters.AddWithValue("$size", (object?)size ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public List<ArtifactRecord> ListByBuild(long buildId)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM artifacts WHERE build_id = $buildId ORDER BY path";
        cmd.Parameters.AddWithValue("$buildId", buildId);
        using var reader = cmd.ExecuteReader();
        var result = new List<ArtifactRecord>();
        while (reader.Read())
        {
            result.Add(new ArtifactRecord
            {
                Id = reader.GetInt64(reader.GetOrdinal("id")),
                BuildId = reader.GetInt64(reader.GetOrdinal("build_id")),
                Path = reader.GetString(reader.GetOrdinal("path")),
                Size = reader.IsDBNull(reader.GetOrdinal("size")) ? null : reader.GetInt64(reader.GetOrdinal("size")),
            });
        }
        return result;
    }
}
