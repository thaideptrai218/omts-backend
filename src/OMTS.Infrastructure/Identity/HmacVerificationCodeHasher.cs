using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using OMTS.Application.Common.Interfaces.Identity;

namespace OMTS.Infrastructure.Identity;

/// <summary>HMAC-SHA256 of "{requestId}:{code}" with a key derived from the JWT signing key (never the key itself).</summary>
internal sealed class HmacVerificationCodeHasher(IOptions<JwtOptions> options) : IVerificationCodeHasher
{
    public string Hash(Guid requestId, string code)
    {
        var key = HMACSHA256.HashData(options.Value.CreateSigningKey().Key, "omts-verification-code"u8);
        return Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"{requestId:N}:{code}")));
    }

    public bool Matches(Guid requestId, string code, string storedHash) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hash(requestId, code)), Encoding.ASCII.GetBytes(storedHash));
}
