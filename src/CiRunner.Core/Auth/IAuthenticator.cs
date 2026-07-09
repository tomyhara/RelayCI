namespace CiRunner.Core.Auth;

public sealed record AuthResult(string Username, string? DisplayName, string? Mail, string? TelephoneNumber);

/// <summary>
/// Identity verification only (spec §9: "認証(誰であるか)は LDAP、認可(何ができるか)はランナー側").
/// Role resolution always goes through user_roles regardless of which implementation verified the
/// password, so LDAP and the Debug-only local-users substitute are authorized identically.
/// </summary>
public interface IAuthenticator
{
    Task<AuthResult?> AuthenticateAsync(string username, string password, CancellationToken ct);
}
