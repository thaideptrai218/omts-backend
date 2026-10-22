using System.Security.Cryptography;
using System.Text;

namespace OMTS.Application.Common;

/// <summary>Random secrets and their stored hashes (verification codes and tokens are never stored in clear).</summary>
public static class SecretHashing
{
    public static string Sha256Hex(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    /// <summary>A 6-digit code, uniformly random.</summary>
    public static string NewSixDigitCode() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

    /// <summary>A 256-bit random token as 64 hex characters.</summary>
    public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
}
