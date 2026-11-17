using OMTS.Domain.Common;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;

namespace OMTS.Domain.Entities.Labor;

/// <summary>
/// Technical entity (SDS): one login of an employee in the employee app on the linked device, backing a refresh token. A
/// table of its own, never <see cref="LoginSession"/>: the employee app is a second kind of caller that no staff session or
/// token can stand in for (BR-LABOR-02). Same rules as a staff session: only a hash of the refresh token is stored and the
/// token is rotated on every refresh; the one a rotation replaced is still accepted for a short grace period.
/// </summary>
public class EmployeeSession : Entity
{
    public Guid EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;

    public Guid EmployeeDeviceId { get; set; }
    public EmployeeDevice EmployeeDevice { get; set; } = null!;

    public string RefreshTokenHash { get; set; } = string.Empty;

    /// <summary>Hash of the token that <see cref="RefreshTokenHash"/> replaced; valid until <see cref="PreviousRefreshTokenGraceEndsAt"/>, after that it is replay.</summary>
    public string? PreviousRefreshTokenHash { get; set; }

    public DateTimeOffset? PreviousRefreshTokenGraceEndsAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastRefreshedAt { get; set; }

    /// <summary>Sliding expiry (30 days).</summary>
    public DateTimeOffset RefreshTokenExpiresAt { get; set; }

    /// <summary>Hard limit regardless of refreshes (90 days).</summary>
    public DateTimeOffset AbsoluteExpiresAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }
    public string? RevokeReason { get; set; }
    public SessionRevokeCause? RevokeCause { get; set; }

    public bool IsUsable(DateTimeOffset now) => RevokedAt is null && now < AbsoluteExpiresAt;

    public bool CanRefresh(DateTimeOffset now) => IsUsable(now) && now < RefreshTokenExpiresAt;

    public bool IsWithinRefreshGrace(string refreshTokenHash, DateTimeOffset now) =>
        PreviousRefreshTokenHash is not null && PreviousRefreshTokenHash == refreshTokenHash && now < PreviousRefreshTokenGraceEndsAt;

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
