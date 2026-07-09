using CiRunner.Core.Config;

namespace CiRunner.Core.Auth;

/// <summary>
/// Fixed username/password list, substituting for a real LDAP server in automated tests (spec §9
/// "テスト用認証"). The caller (host startup) is responsible for the Debug-only enforcement -
/// this class has no opinion on build configuration, it just checks a list.
/// </summary>
public sealed class LocalUsersAuthenticator : IAuthenticator
{
    private readonly List<LocalUser> _users;

    public LocalUsersAuthenticator(List<LocalUser> users)
    {
        _users = users;
    }

    public Task<AuthResult?> AuthenticateAsync(string username, string password, CancellationToken ct)
    {
        var match = _users.FirstOrDefault(u => u.Username == username && u.Password == password);
        return Task.FromResult(match is null ? null : new AuthResult(match.Username, match.Username, $"{match.Username}@example.test", null));
    }
}
