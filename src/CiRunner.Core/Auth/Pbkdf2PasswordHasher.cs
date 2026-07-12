using System.Security.Cryptography;

namespace CiRunner.Core.Auth;

/// <summary>
/// PBKDF2-SHA256 password hashing for `auth.mode = "local"` (spec §9): at least 100,000 iterations,
/// a random per-user salt, encoded as `<iterations>.<salt/base64>.<hash/base64>`. Never stores or
/// logs the plaintext password.
/// </summary>
public static class Pbkdf2PasswordHasher
{
    public const int MinIterations = 100_000;
    private const int SaltSizeBytes = 16;
    private const int HashSizeBytes = 32;

    public static string Hash(string password, int iterations = MinIterations)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSizeBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, HashSizeBytes);
        return $"{iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    /// <summary>Constant-time verification. Returns false (never throws) for a malformed stored value,
    /// e.g. data corruption or a row written by an unrelated mechanism.</summary>
    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split('.');
        if (parts.Length != 3)
        {
            return false;
        }
        if (!int.TryParse(parts[0], out var iterations) || iterations < 1)
        {
            return false;
        }

        byte[] salt, expectedHash;
        try
        {
            salt = Convert.FromBase64String(parts[1]);
            expectedHash = Convert.FromBase64String(parts[2]);
        }
        catch (FormatException)
        {
            return false;
        }

        var actualHash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expectedHash.Length);
        return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
    }
}
