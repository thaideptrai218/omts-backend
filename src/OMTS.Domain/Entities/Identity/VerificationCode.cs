using OMTS.Domain.Common;
using OMTS.Domain.Enums;

namespace OMTS.Domain.Entities.Identity;

/// <summary>
/// Technical entity (SDS): one verification request (ACS-06) proving possession of a phone number or
/// email. Its 6-digit code is valid 5 minutes, allows at most 5 wrong entries and 3 sends at least 60
/// seconds apart (NFR-01). A correct code yields a single-use verification token, valid 10 minutes, that
/// the calling use case consumes. Only hashes of the code and the token are stored.
/// </summary>
public class VerificationCode : Entity
{
    public const int MaxFailedAttempts = 5;
    public const int MaxSends = 3;
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan ResendInterval = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(10);

    /// <summary>Null during trader registration, before the account exists.</summary>
    public Guid? AccountId { get; set; }
    public Account? Account { get; set; }

    public VerificationPurpose Purpose { get; set; }
    public VerificationChannel Channel { get; set; }

    /// <summary>Phone number or email the code was sent to (normalised).</summary>
    public string Target { get; set; } = string.Empty;

    public string CodeHash { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset LastSentAt { get; set; }
    public int SendCount { get; set; } = 1;
    public int FailedAttempts { get; set; }

    /// <summary>When the code was entered correctly; it can never be used again.</summary>
    public DateTimeOffset? ConsumedAt { get; set; }

    /// <summary>A newer request for the same purpose (other target or delivery method) replaced this one.</summary>
    public DateTimeOffset? CancelledAt { get; set; }

    public string? TokenHash { get; set; }
    public DateTimeOffset? TokenExpiresAt { get; set; }
    public DateTimeOffset? TokenConsumedAt { get; set; }

    /// <summary>
    /// Concurrency token: parallel wrong entries or parallel uses of one token cannot both be saved, so the
    /// attempt limit and single use hold under races.
    /// </summary>
    public byte[] RowVersion { get; set; } = [];

    public bool IsLocked => FailedAttempts >= MaxFailedAttempts;

    public int SendsLeft => Math.Max(0, MaxSends - SendCount);

    public bool IsOpen => ConsumedAt is null && CancelledAt is null;

    public bool IsUsable(DateTimeOffset now) => IsOpen && now < ExpiresAt && !IsLocked;

    public bool CanResend(DateTimeOffset now) => IsOpen && !IsLocked && SendCount < MaxSends && now - LastSentAt >= ResendInterval;

    public DateTimeOffset NextResendAt => LastSentAt + ResendInterval;

    public bool IsTokenUsable(DateTimeOffset now) =>
        TokenHash is not null && TokenConsumedAt is null && CancelledAt is null && now < TokenExpiresAt;
}
