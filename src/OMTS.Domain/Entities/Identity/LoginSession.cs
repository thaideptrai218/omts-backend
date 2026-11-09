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

    /// <summary>
    /// Hash of the refresh token that <see cref="RefreshTokenHash"/> replaced. It stays valid until
    /// <see cref="PreviousRefreshTokenGraceEndsAt"/>, so a client whose rotation response was lost or raced
    /// can still ask once more; any older token is replay.
    /// </summary>
    public string? PreviousRefreshTokenHash { get; set; }

    public DateTimeOffset? PreviousRefreshTokenGraceEndsAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastRefreshedAt { get; set; }

    /// <summary>Sliding expiry: 7 days (web) / 30 days (mobile).</summary>
    public DateTimeOffset RefreshTokenExpiresAt { get; set; }

    /// <summary>Hard limit regardless of refreshes: 30 days (web) / 90 days (mobile).</summary>
    public DateTimeOffset AbsoluteExpiresAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }
    public string? RevokeReason { get; set; }

    /// <summary>Null while the session is open; a revoked session of an older version also has none and counts as <see cref="SessionRevokeCause.Revoked"/>.</summary>
    public SessionRevokeCause? RevokeCause { get; set; }

    public AuthMethod AuthMethod { get; set; }

    /// <summary>
    /// Keyed hash of the random id the browser or app install keeps (never the id itself), so the sessions of one device can be
    /// listed and revoked together. Null when the client sent no usable id; such sessions are never grouped.
    /// </summary>
    public string? DeviceKey { get; set; }

    public string? DeviceName { get; set; }
    public string? UserAgent { get; set; }
    public string? IpAddress { get; set; }

    public bool IsUsable(DateTimeOffset now) => RevokedAt is null && now < AbsoluteExpiresAt;

    public bool CanRefresh(DateTimeOffset now) => IsUsable(now) && now < RefreshTokenExpiresAt;

    /// <summary>True for the immediately superseded refresh token, until its grace period ends.</summary>
    public bool IsWithinRefreshGrace(string refreshTokenHash, DateTimeOffset now) =>
        PreviousRefreshTokenHash is not null
        && PreviousRefreshTokenHash == refreshTokenHash
        && now < PreviousRefreshTokenGraceEndsAt;

    public void Rotate(string newRefreshTokenHash, DateTimeOffset now, TimeSpan refreshLifetime, TimeSpan previousTokenGrace)
    {
        PreviousRefreshTokenHash = RefreshTokenHash;
        PreviousRefreshTokenGraceEndsAt = now + previousTokenGrace;
        RefreshTokenHash = newRefreshTokenHash;
        LastRefreshedAt = now;
        var slidingExpiry = now + refreshLifetime;
        RefreshTokenExpiresAt = slidingExpiry < AbsoluteExpiresAt ? slidingExpiry : AbsoluteExpiresAt;
    }

    public void Revoke(DateTimeOffset now, string reason, SessionRevokeCause cause = SessionRevokeCause.Revoked)
    {
        if (RevokedAt is not null)
        {
            return;
        }

        RevokedAt = now;
        RevokeReason = reason;
        RevokeCause = cause;
    }
}
