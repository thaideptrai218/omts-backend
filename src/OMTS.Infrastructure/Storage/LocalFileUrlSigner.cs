using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace OMTS.Infrastructure.Storage;

/// <summary>
/// Signs and checks the expiring download URLs of the Local provider (HMAC-SHA256 over key and expiry),
/// the local equivalent of an Azure SAS URL.
/// </summary>
public sealed class LocalFileUrlSigner
{
    public const string DownloadPath = "/api/files/local/";

    private readonly byte[] _key;

    public LocalFileUrlSigner(IOptions<StorageOptions> options)
    {
        var configured = options.Value.Local.SigningKey;
        _key = string.IsNullOrEmpty(configured) ? RandomNumberGenerator.GetBytes(32) : Encoding.UTF8.GetBytes(configured);
    }

    public string CreateUrl(string key, DateTimeOffset expiresAt)
    {
        var expires = expiresAt.ToUnixTimeSeconds();
        return $"{DownloadPath}{key}?expires={expires}&sig={Sign(key, expires)}";
    }

    public bool IsValid(string key, long expires, string? signature, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(signature) || DateTimeOffset.FromUnixTimeSeconds(expires) < now)
        {
            return false;
        }

        var expected = Encoding.ASCII.GetBytes(Sign(key, expires));
        var actual = Encoding.ASCII.GetBytes(signature);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private string Sign(string key, long expires) =>
        Convert.ToHexString(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes($"{key}|{expires}"))).ToLowerInvariant();
}
