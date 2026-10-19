using OMTS.Domain.Common;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Domain.Entities.Identity;

/// <summary>E02 – A person who can log in; the subject that receives zone–role pairs.</summary>
public class Account : AuditableEntity, IHasStatus
{
    public static readonly TimeSpan TemporaryPasswordLifetime = TimeSpan.FromHours(24);
    public static readonly TimeSpan TemporaryLockDuration = TimeSpan.FromMinutes(15);
    public const int MaxFailedLogins = 5;

    public string DisplayName { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;

    /// <summary>Login phone number, unique system-wide (BR-32, BR-34).</summary>
    public string? PhoneNumber { get; set; }
    public DateTimeOffset? PhoneVerifiedAt { get; set; }

    /// <summary>Unique system-wide and usable for login once verified (BR-32).</summary>
    public string? Email { get; set; }
    public DateTimeOffset? EmailVerifiedAt { get; set; }

    /// <summary>Salted one-way hash of the password (NFR-01); never the password itself.</summary>
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>Set when a temporary password is issued (UC11, UC72, UC74); expires after 24 hours.</summary>
    public DateTimeOffset? TemporaryPasswordIssuedAt { get; set; }
    public bool MustChangePassword { get; set; }

    /// <summary>Consecutive failed logins; the 5th causes a 15-minute temporary lock (UC02 – 2e).</summary>
    public int FailedLoginCount { get; set; }

    /// <summary>End of the temporary lock. Derived condition only – the status stays Active (Table 3.4-3).</summary>
    public DateTimeOffset? LockoutEndAt { get; set; }

    public DateTimeOffset? LastLoginAt { get; set; }

    public SubjectType SubjectType { get; set; }

    public int StatusId { get; set; } = StatusIds.Account.Active;
    public Status Status { get; set; } = null!;

    /// <summary>On/off per notification category and channel (UC06); mandatory categories cannot be turned off.</summary>
    public List<NotificationPreference> NotificationPreferences { get; set; } = [];

    public Employee? Employee { get; set; }
    public Customer? Customer { get; set; }
    public ICollection<AccessGrant> AccessGrants { get; set; } = [];
    public ICollection<LoginSession> LoginSessions { get; set; } = [];

    public bool IsTemporarilyLocked(DateTimeOffset now) => LockoutEndAt > now;

    public bool IsTemporaryPasswordExpired(DateTimeOffset now) =>
        MustChangePassword && now - TemporaryPasswordIssuedAt > TemporaryPasswordLifetime;

    public void RegisterFailedLogin(DateTimeOffset now)
    {
        FailedLoginCount++;
        if (FailedLoginCount >= MaxFailedLogins)
        {
            LockoutEndAt = now + TemporaryLockDuration;
            FailedLoginCount = 0;
        }
    }

    public void RegisterSuccessfulLogin(DateTimeOffset now)
    {
        FailedLoginCount = 0;
        LockoutEndAt = null;
        LastLoginAt = now;
    }
}
