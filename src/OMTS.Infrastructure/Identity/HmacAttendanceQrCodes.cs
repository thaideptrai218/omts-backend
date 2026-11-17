using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using OMTS.Application.Common.Interfaces.Identity;

namespace OMTS.Infrastructure.Identity;

/// <summary>
/// "OMTS-ATT.{employeeId}.{expiryUnixSeconds}.{mac}": HMAC-SHA256 of the first three parts with a key derived from the JWT signing key
/// (never the key itself). It is read only while the expiry is in the future.
/// </summary>
internal sealed class HmacAttendanceQrCodes(IOptions<JwtOptions> options, TimeProvider timeProvider) : IAttendanceQrCodes
{
    private const string Prefix = "OMTS-ATT";
    private const long QrClockSkewSeconds = 5;

    public AttendanceQrCode Issue(Guid employeeId)
    {
        var expiresAt = timeProvider.GetUtcNow().Add(IAttendanceQrCodes.Lifetime);
        var body = $"{Prefix}.{employeeId:N}.{expiresAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}";
        return new AttendanceQrCode($"{body}.{Mac(body)}", expiresAt);
    }

    public Guid? Read(string? code)
    {
        var parts = code?.Trim().Split('.');
        if (parts is not [Prefix, var id, var expiry, var mac]
            || !Guid.TryParseExact(id, "N", out var employeeId)
            || !long.TryParse(expiry, NumberStyles.None, CultureInfo.InvariantCulture, out var expiresAt))
        {
            return null;
        }

        var expected = Mac($"{Prefix}.{id}.{expiry}");
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(mac)))
        {
            return null;
        }

        // Current: not past its expiry, and not issued in the future (an expiry further away than one lifetime, with a few seconds of clock skew, was not made by Issue now) (c5 of the review of 13/11/2026).
        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        return now < expiresAt && expiresAt <= now + (long)IAttendanceQrCodes.Lifetime.TotalSeconds + QrClockSkewSeconds ? employeeId : null;
    }

    private string Mac(string body)
    {
        var key = HMACSHA256.HashData(options.Value.CreateSigningKey().Key, "omts-attendance-qr"u8);
        return Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(body)));
    }
}
