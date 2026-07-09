namespace CiRunner.Core.Models;

public sealed class UserRoleRecord
{
    public required string Username { get; set; }
    public required string Role { get; set; }
    public required string UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}

public static class Role
{
    public const string Admin = "admin";
    public const string Operator = "operator";
    public const string Viewer = "viewer";

    private static readonly string[] Order = { Viewer, Operator, Admin };

    /// <summary>True if `role` grants at least the privileges of `atLeast` (admin > operator > viewer).</summary>
    public static bool Satisfies(string? role, string atLeast)
    {
        var have = Array.IndexOf(Order, role);
        var need = Array.IndexOf(Order, atLeast);
        return have >= 0 && need >= 0 && have >= need;
    }
}

public sealed class ApiTokenRecord
{
    public long Id { get; set; }
    public required string Name { get; set; }
    public required string TokenHash { get; set; }
    public required string Role { get; set; }
    public required string CreatedAt { get; set; }
    public required string CreatedBy { get; set; }
    public string? LastUsedAt { get; set; }
    public string? RevokedAt { get; set; }
}

public sealed class AuditLogEntry
{
    public long Id { get; set; }
    public required string At { get; set; }
    public required string Username { get; set; }
    public required string Action { get; set; }
    public string? Target { get; set; }
    public string? BeforeJson { get; set; }
    public string? AfterJson { get; set; }
}
