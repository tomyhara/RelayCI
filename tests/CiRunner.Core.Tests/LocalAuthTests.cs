using CiRunner.Core.Auth;
using CiRunner.Core.Data;
using CiRunner.Core.Tests.Support;
using Xunit;

namespace CiRunner.Core.Tests;

/// <summary>Unit-level coverage for auth.mode="local" (spec §9): password hashing format, the
/// local_users repository, and the login-attempt cooldown, all independent of the HTTP layer
/// (that's covered by CiRunner.Host.Tests/LocalAuthApiTests instead).</summary>
public class LocalAuthTests
{
    // ---- Pbkdf2PasswordHasher ----

    [Fact]
    public void Hash_ProducesIterationsSaltHashFormat_WithAtLeast100kIterations()
    {
        var encoded = Pbkdf2PasswordHasher.Hash("correct horse battery staple");

        var parts = encoded.Split('.');
        Assert.Equal(3, parts.Length);
        var iterations = int.Parse(parts[0]);
        Assert.True(iterations >= 100_000, $"expected >= 100000 iterations, got {iterations}");
        Assert.True(Convert.FromBase64String(parts[1]).Length > 0); // salt
        Assert.True(Convert.FromBase64String(parts[2]).Length > 0); // hash
    }

    [Fact]
    public void Hash_IsSalted_SamePasswordProducesDifferentEncodedValues()
    {
        var a = Pbkdf2PasswordHasher.Hash("same-password");
        var b = Pbkdf2PasswordHasher.Hash("same-password");

        Assert.NotEqual(a, b); // different random salts
        Assert.True(Pbkdf2PasswordHasher.Verify("same-password", a));
        Assert.True(Pbkdf2PasswordHasher.Verify("same-password", b));
    }

    [Fact]
    public void Verify_RoundTrips_CorrectAndWrongPassword()
    {
        var encoded = Pbkdf2PasswordHasher.Hash("s3cr3t!");

        Assert.True(Pbkdf2PasswordHasher.Verify("s3cr3t!", encoded));
        Assert.False(Pbkdf2PasswordHasher.Verify("wrong", encoded));
    }

    [Theory]
    [InlineData("not-the-right-format")]
    [InlineData("100000.onlytwoparts")]
    [InlineData("notanumber.c2FsdA==.aGFzaA==")]
    [InlineData("100000.not-base64!!.aGFzaA==")]
    public void Verify_MalformedStoredValue_ReturnsFalseRatherThanThrowing(string malformed)
    {
        Assert.False(Pbkdf2PasswordHasher.Verify("anything", malformed));
    }

    // ---- LocalUserRepository ----

    [Fact]
    public void Add_ThenFindByUsername_RoundTrips()
    {
        using var temp = new TempDatabase();
        var repo = new LocalUserRepository(temp.Db);

        var hash = Pbkdf2PasswordHasher.Hash("pw12345678");
        repo.Add("alice", hash, "Alice Example");

        var found = repo.FindByUsername("alice");
        Assert.NotNull(found);
        Assert.Equal(hash, found!.PasswordHash);
        Assert.Equal("Alice Example", found.DisplayName);
        Assert.True(found.Enabled);
    }

    [Fact]
    public void SetEnabled_DisablesUser()
    {
        using var temp = new TempDatabase();
        var repo = new LocalUserRepository(temp.Db);
        repo.Add("bob", Pbkdf2PasswordHasher.Hash("pw12345678"), null);

        Assert.True(repo.SetEnabled("bob", false));

        Assert.False(repo.FindByUsername("bob")!.Enabled);
    }

    [Fact]
    public void UpdatePassword_ChangesStoredHash()
    {
        using var temp = new TempDatabase();
        var repo = new LocalUserRepository(temp.Db);
        var original = Pbkdf2PasswordHasher.Hash("pw12345678");
        repo.Add("carol", original, null);

        var updated = Pbkdf2PasswordHasher.Hash("newpassword1");
        Assert.True(repo.UpdatePassword("carol", updated));

        Assert.Equal(updated, repo.FindByUsername("carol")!.PasswordHash);
    }

    [Fact]
    public void Delete_RemovesUser()
    {
        using var temp = new TempDatabase();
        var repo = new LocalUserRepository(temp.Db);
        repo.Add("dave", Pbkdf2PasswordHasher.Hash("pw12345678"), null);

        Assert.True(repo.Delete("dave"));
        Assert.Null(repo.FindByUsername("dave"));
    }

    [Fact]
    public void ListAll_ReturnsSortedByUsername()
    {
        using var temp = new TempDatabase();
        var repo = new LocalUserRepository(temp.Db);
        repo.Add("zack", Pbkdf2PasswordHasher.Hash("pw12345678"), null);
        repo.Add("amy", Pbkdf2PasswordHasher.Hash("pw12345678"), null);

        var names = repo.ListAll().Select(u => u.Username).ToList();

        Assert.Equal(new[] { "amy", "zack" }, names);
    }

    // ---- LocalAccountAuthenticator (spec §9 login flow + lockout) ----

    [Fact]
    public async Task AuthenticateAsync_CorrectCredentials_ReturnsAuthResultWithNullMail()
    {
        using var temp = new TempDatabase();
        var repo = new LocalUserRepository(temp.Db);
        repo.Add("erin", Pbkdf2PasswordHasher.Hash("pw12345678"), "Erin Example");
        var auth = new LocalAccountAuthenticator(repo);

        var result = await auth.AuthenticateAsync("erin", "pw12345678", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("erin", result!.Username);
        Assert.Equal("Erin Example", result.DisplayName);
        Assert.Null(result.Mail); // spec §9: mail/telephoneNumber are always empty under mode=local
        Assert.Null(result.TelephoneNumber);
    }

    [Fact]
    public async Task AuthenticateAsync_WrongPassword_ReturnsNull()
    {
        using var temp = new TempDatabase();
        var repo = new LocalUserRepository(temp.Db);
        repo.Add("frank", Pbkdf2PasswordHasher.Hash("pw12345678"), null);
        var auth = new LocalAccountAuthenticator(repo);

        var result = await auth.AuthenticateAsync("frank", "wrong-password", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task AuthenticateAsync_DisabledUser_IsRejectedEvenWithCorrectPassword()
    {
        using var temp = new TempDatabase();
        var repo = new LocalUserRepository(temp.Db);
        repo.Add("grace", Pbkdf2PasswordHasher.Hash("pw12345678"), null);
        repo.SetEnabled("grace", false);
        var auth = new LocalAccountAuthenticator(repo);

        var result = await auth.AuthenticateAsync("grace", "pw12345678", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task AuthenticateAsync_UnknownUsername_ReturnsNull()
    {
        using var temp = new TempDatabase();
        var repo = new LocalUserRepository(temp.Db);
        var auth = new LocalAccountAuthenticator(repo);

        var result = await auth.AuthenticateAsync("ghost", "whatever", CancellationToken.None);

        Assert.Null(result);
    }

    // Spec §9: "同一ユーザー名について連続 5 回失敗で 30 秒のクールダウン". Uses a short injected
    // cooldown so the recovery half of this test doesn't need a real 30-second sleep.
    [Fact]
    public async Task AuthenticateAsync_FiveFailures_LocksOutEvenCorrectPassword_ThenRecoversAfterCooldown()
    {
        using var temp = new TempDatabase();
        var repo = new LocalUserRepository(temp.Db);
        repo.Add("henry", Pbkdf2PasswordHasher.Hash("pw12345678"), null);
        var auth = new LocalAccountAuthenticator(repo, maxFailures: 5, cooldown: TimeSpan.FromMilliseconds(200));

        for (var i = 0; i < 5; i++)
        {
            var fail = await auth.AuthenticateAsync("henry", "wrong", CancellationToken.None);
            Assert.Null(fail);
        }

        // Still within the cooldown window: even the correct password is rejected.
        var stillLocked = await auth.AuthenticateAsync("henry", "pw12345678", CancellationToken.None);
        Assert.Null(stillLocked);

        await Task.Delay(300);

        var recovered = await auth.AuthenticateAsync("henry", "pw12345678", CancellationToken.None);
        Assert.NotNull(recovered);
    }

    [Fact]
    public async Task AuthenticateAsync_FailuresBelowThreshold_DoNotLockOut()
    {
        using var temp = new TempDatabase();
        var repo = new LocalUserRepository(temp.Db);
        repo.Add("iris", Pbkdf2PasswordHasher.Hash("pw12345678"), null);
        var auth = new LocalAccountAuthenticator(repo, maxFailures: 5, cooldown: TimeSpan.FromSeconds(30));

        for (var i = 0; i < 4; i++)
        {
            await auth.AuthenticateAsync("iris", "wrong", CancellationToken.None);
        }

        var result = await auth.AuthenticateAsync("iris", "pw12345678", CancellationToken.None);
        Assert.NotNull(result); // 4 failures is below the 5-failure threshold, so no lockout yet
    }

    [Fact]
    public async Task AuthenticateAsync_SuccessfulLogin_ResetsFailureCount()
    {
        using var temp = new TempDatabase();
        var repo = new LocalUserRepository(temp.Db);
        repo.Add("jack", Pbkdf2PasswordHasher.Hash("pw12345678"), null);
        var auth = new LocalAccountAuthenticator(repo, maxFailures: 5, cooldown: TimeSpan.FromSeconds(30));

        for (var i = 0; i < 4; i++)
        {
            await auth.AuthenticateAsync("jack", "wrong", CancellationToken.None);
        }
        Assert.NotNull(await auth.AuthenticateAsync("jack", "pw12345678", CancellationToken.None)); // resets counter

        for (var i = 0; i < 4; i++)
        {
            await auth.AuthenticateAsync("jack", "wrong", CancellationToken.None);
        }
        // Only 4 failures since the reset - still below threshold, so not locked out.
        Assert.NotNull(await auth.AuthenticateAsync("jack", "pw12345678", CancellationToken.None));
    }
}
