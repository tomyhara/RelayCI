using CiRunner.Core.Models;
using Microsoft.Data.Sqlite;

namespace CiRunner.Core.Data;

public sealed class UserRoleRepository
{
    private readonly CiDatabase _db;

    public UserRoleRepository(CiDatabase db)
    {
        _db = db;
    }

    public bool IsEmpty()
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM user_roles";
        return Convert.ToInt64(cmd.ExecuteScalar()) == 0;
    }

    public UserRoleRecord? Find(string username)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM user_roles WHERE username = $username";
        cmd.Parameters.AddWithValue("$username", username);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public List<UserRoleRecord> ListAll()
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM user_roles ORDER BY username";
        using var reader = cmd.ExecuteReader();
        var result = new List<UserRoleRecord>();
        while (reader.Read())
        {
            result.Add(Map(reader));
        }
        return result;
    }

    public int CountAdmins()
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM user_roles WHERE role = $admin";
        cmd.Parameters.AddWithValue("$admin", Models.Role.Admin);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public void Upsert(string username, string role, string updatedBy)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO user_roles (username, role, updated_at, updated_by) VALUES ($username, $role, $now, $by)
            ON CONFLICT(username) DO UPDATE SET role = $role, updated_at = $now, updated_by = $by
            """;
        cmd.Parameters.AddWithValue("$username", username);
        cmd.Parameters.AddWithValue("$role", role);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("o"));
        cmd.Parameters.AddWithValue("$by", updatedBy);
        cmd.ExecuteNonQuery();
    }

    public void Delete(string username)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM user_roles WHERE username = $username";
        cmd.Parameters.AddWithValue("$username", username);
        cmd.ExecuteNonQuery();
    }

    private static UserRoleRecord Map(SqliteDataReader r) => new()
    {
        Username = r.GetString(r.GetOrdinal("username")),
        Role = r.GetString(r.GetOrdinal("role")),
        UpdatedAt = r.GetString(r.GetOrdinal("updated_at")),
        UpdatedBy = r.IsDBNull(r.GetOrdinal("updated_by")) ? null : r.GetString(r.GetOrdinal("updated_by")),
    };
}
