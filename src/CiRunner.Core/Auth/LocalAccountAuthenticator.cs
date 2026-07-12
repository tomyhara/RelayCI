using System.Collections.Concurrent;
using CiRunner.Core.Data;

namespace CiRunner.Core.Auth;

/// <summary>
/// `auth.mode = "local"` (spec §9): verifies against the `local_users` table instead of LDAP.
/// `displayName` comes from the row; `mail`/`telephoneNumber` are always null (no LDAP directory to
/// pull them from), so `CI_USER_EMAIL` stays unset for manual triggers under this mode.
///
/// Also owns the login-attempt cooldown (spec §9 "同一ユーザー名について連続 5 回失敗で 30 秒の
/// クールダウン"): in-memory, per-username, reset on process restart. This is deliberately scoped to
/// local accounts only - LDAP already has its own lockout policy on the directory side.
/// </summary>
public sealed class LocalAccountAuthenticator : IAuthenticator
{
    private readonly LocalUserRepository _users;
    private readonly int _maxFailures;
    private readonly TimeSpan _cooldown;
    private readonly ConcurrentDictionary<string, AttemptState> _attempts = new();

    public LocalAccountAuthenticator(LocalUserRepository users, int maxFailures = 5, TimeSpan? cooldown = null)
    {
        _users = users;
        _maxFailures = maxFailures;
        _cooldown = cooldown ?? TimeSpan.FromSeconds(30);
    }

    public Task<AuthResult?> AuthenticateAsync(string username, string password, CancellationToken ct)
    {
        if (IsLockedOut(username))
        {
            return Task.FromResult<AuthResult?>(null);
        }

        var user = _users.FindByUsername(username);
        if (user is null || !user.Enabled || !Pbkdf2PasswordHasher.Verify(password, user.PasswordHash))
        {
            RecordFailure(username);
            return Task.FromResult<AuthResult?>(null);
        }

        _attempts.TryRemove(username, out _);
        return Task.FromResult<AuthResult?>(new AuthResult(user.Username, user.DisplayName, null, null));
    }

    private bool IsLockedOut(string username)
    {
        if (!_attempts.TryGetValue(username, out var state))
        {
            return false;
        }
        if (state.LockedUntil is { } until)
        {
            if (DateTimeOffset.UtcNow < until)
            {
                return true;
            }
            // Cooldown elapsed: clear the record so the next attempt starts a fresh count.
            _attempts.TryRemove(username, out _);
        }
        return false;
    }

    private void RecordFailure(string username)
    {
        _attempts.AddOrUpdate(
            username,
            _ => new AttemptState(1, null),
            (_, existing) =>
            {
                var count = existing.FailureCount + 1;
                var lockedUntil = count >= _maxFailures ? DateTimeOffset.UtcNow.Add(_cooldown) : (DateTimeOffset?)null;
                return new AttemptState(count, lockedUntil);
            });
    }

    private sealed record AttemptState(int FailureCount, DateTimeOffset? LockedUntil);
}
