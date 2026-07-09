using System.Text.Json.Serialization;

namespace CiRunner.Core.Config;

public sealed class RunnerConfig
{
    public string Bind { get; set; } = "0.0.0.0";
    public int Port { get; set; } = 8080;
    public GitConfig Git { get; set; } = new();
    public GhesConfig Ghes { get; set; } = new();
    public AuthConfig Auth { get; set; } = new();

    /// <summary>Root directory containing jobs/, hooks/, workspaces/, logs/, data/. Not part of config.json; set from --root or CWD.</summary>
    [JsonIgnore]
    public string RootDir { get; set; } = Directory.GetCurrentDirectory();
}

public sealed class GitConfig
{
    public string ExePath { get; set; } = "git";
}

public sealed class GhesConfig
{
    public string? BaseUrl { get; set; }
    public string? Pat { get; set; }
}

public sealed class AuthConfig
{
    public LdapConfig Ldap { get; set; } = new();
    public List<string> InitialAdmins { get; set; } = new();
    public string DefaultRole { get; set; } = "viewer";
    public int SessionHours { get; set; } = 12;

    /// <summary>Debug-build-only LDAP substitute. Release builds must reject this key at startup (spec §9).</summary>
    public List<LocalUser>? LocalUsers { get; set; }
}

public sealed class LdapConfig
{
    public string? Server { get; set; }
    public string? SearchBase { get; set; }
    public string UserFilter { get; set; } = "(sAMAccountName={username})";
    public List<string> Attributes { get; set; } = new() { "displayName", "mail", "telephoneNumber" };
    public string? ServiceBindDn { get; set; }
    public string? ServiceBindPassword { get; set; }
}

public sealed class LocalUser
{
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string Role { get; set; } = "viewer";
}
