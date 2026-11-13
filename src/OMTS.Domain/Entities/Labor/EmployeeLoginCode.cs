using OMTS.Domain.Common;
using OMTS.Domain.Entities.Identity;

namespace OMTS.Domain.Entities.Labor;

/// <summary>
/// Technical entity (SDS, outside the SRS data model, BR-LABOR-02): the login code a Zone Manager or the Cooperative Owner
/// issues for one employee. 6 digits, valid 5 minutes, cancelled by the 5th wrong entry, single use; a new code cancels the
/// earlier one. Only a keyed hash is stored (<c>IVerificationCodeHasher</c>, keyed by this row's id), never the code, and
/// the code is never written to the audit log (GBR-02).
/// </summary>
public class EmployeeLoginCode : Entity
{
    public const int CodeLength = 6;
    public const int MaxFailedAttempts = 5;
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    public Guid EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;

    public string CodeHash { get; set; } = string.Empty;

    public Guid IssuedByAccountId { get; set; }
    public Account IssuedBy { get; set; } = null!;

    public DateTimeOffset IssuedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }

    public int FailedAttempts { get; set; }

    /// <summary>When the code was entered correctly; it can never be used again.</summary>
    public DateTimeOffset? ConsumedAt { get; set; }

    /// <summary>A newer code replaced this one, or the 5th wrong entry cancelled it.</summary>
    public DateTimeOffset? CancelledAt { get; set; }

    /// <summary>Concurrency token: parallel wrong entries or parallel uses of one code cannot both be saved, so the limit and the single use hold under races.</summary>
    public byte[] RowVersion { get; set; } = [];

    public bool IsLocked => FailedAttempts >= MaxFailedAttempts;

    public bool IsOpen => ConsumedAt is null && CancelledAt is null;

    public bool IsUsable(DateTimeOffset now) => IsOpen && now < ExpiresAt && !IsLocked;
}
