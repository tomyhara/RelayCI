namespace CiRunner.Core.Models;

/// <summary>Row of `local_users` (spec §7/§9, auth.mode = "local"). PasswordHash is always the PBKDF2-SHA256
/// encoded form `<iterations>.<salt/base64>.<hash/base64>` - never plaintext.</summary>
public sealed class LocalUserRecord
{
    public required string Username { get; set; }
    public required string PasswordHash { get; set; }
    public string? DisplayName { get; set; }
    public bool Enabled { get; set; } = true;
    public required string CreatedAt { get; set; }
    public required string UpdatedAt { get; set; }
}
