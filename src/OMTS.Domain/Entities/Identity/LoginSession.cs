using OMTS.Domain.Common;
using OMTS.Domain.Enums;

namespace OMTS.Domain.Entities.Identity;

/// <summary>
/// Technical entity (SDS, not part of the SRS model): one login session backing a refresh token.
/// Access tokens carry the session id, so revoking the session also invalidates them (NFR-01).
/// Only a hash of the refresh token is stored; the token is rotated on every refresh.
/// </summary>
public class LoginSession : Entity
{
    public Guid AccountId { get; set; }
    public Account Account { get; set; } = null!;

    public ClientType ClientType { get; set; }

    public string RefreshTokenHash { get; set; } = string.Empty;

    /// <summary>Hash replaced by the last rotation; presenting it again signals a stolen token.</summary>
    public string? PreviousRefreshTokenHash { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastRefreshedAt { get; set; }

    /// <summary>Sliding expiry: 7 days (web) / 30 days (mobile).</summary>
    public DateTimeOffset RefreshTokenExpiresAt { get; set; }

    /// <summary>Hard limit regardless of refreshes: 30 days (web) / 90 days (mobile).</summary>
    public DateTimeOffset AbsoluteExpiresAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }
    public string? RevokeReason { get; set; }

    public string? DeviceName { get; set; }
    public string? UserAgent { get; set; }
    public string? IpAddress { get; set; }

    public bool IsUsable(DateTimeOffset now) => RevokedAt is null && now < AbsoluteExpiresAt;

    public bool CanRefresh(DateTimeOffset now) => IsUsable(now) && now < RefreshTokenExpiresAt;

    public void Rotate(string newRefreshTokenHash, DateTimeOffset now, TimeSpan refreshLifetime)
    {
        PreviousRefreshTokenHash = RefreshTokenHash;
        RefreshTokenHash = newRefreshTokenHash;
        LastRefreshedAt = now;
        var slidingExpiry = now + refreshLifetime;
        RefreshTokenExpiresAt = slidingExpiry < AbsoluteExpiresAt ? slidingExpiry : AbsoluteExpiresAt;
    }

    public void Revoke(DateTimeOffset now, string reason)
    {
        if (RevokedAt is not null)
        {
            return;
        }

        RevokedAt = now;
        RevokeReason = reason;
    }
}
