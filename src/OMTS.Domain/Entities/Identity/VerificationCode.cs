using OMTS.Domain.Common;
using OMTS.Domain.Enums;

namespace OMTS.Domain.Entities.Identity;

/// <summary>
/// Technical entity (SDS): a 6-digit code proving possession of a phone number or email (UC08).
/// Valid 5 minutes, at most 5 wrong entries and 3 sends at least 60 seconds apart (NFR-01).
/// Only a hash of the code is stored.
/// </summary>
public class VerificationCode : Entity
{
    public const int MaxFailedAttempts = 5;
    public const int MaxSends = 3;
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan ResendInterval = TimeSpan.FromSeconds(60);

    /// <summary>Null during trader registration, before the account exists.</summary>
    public Guid? AccountId { get; set; }
    public Account? Account { get; set; }

    public VerificationPurpose Purpose { get; set; }
    public VerificationChannel Channel { get; set; }

    /// <summary>Phone number or email the code was sent to.</summary>
    public string Target { get; set; } = string.Empty;

    public string CodeHash { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset LastSentAt { get; set; }
    public int SendCount { get; set; } = 1;
    public int FailedAttempts { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }

    public bool IsUsable(DateTimeOffset now) => ConsumedAt is null && now < ExpiresAt && FailedAttempts < MaxFailedAttempts;

    public bool CanResend(DateTimeOffset now) => ConsumedAt is null && SendCount < MaxSends && now - LastSentAt >= ResendInterval;
}
