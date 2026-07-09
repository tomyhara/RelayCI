using System.Security.Cryptography;
using System.Text;

namespace CiRunner.Core.Auth;

/// <summary>
/// Tokens are high-entropy random values, so a plain fast hash (not bcrypt/PBKDF2) is standard
/// practice here - brute force against the hash is infeasible regardless of iteration count.
/// </summary>
public static class ApiTokenHasher
{
    public static string GenerateToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
