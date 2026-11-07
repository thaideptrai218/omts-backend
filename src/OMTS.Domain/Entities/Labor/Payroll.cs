using OMTS.Domain.Common;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Platform;

namespace OMTS.Domain.Entities.Labor;

/// <summary>
/// The payroll of one pay period (LABOR-12, LABOR-13, BR-LABOR-10). This is the header only, which the period lock reads
/// (BR-LABOR-13); creating, sending and finalizing it, and its lines, come with LABOR-12. Its row version serializes those
/// actions against a dated change (see PayPeriodLock). The name of the period comes from its end date.
/// </summary>
public class Payroll : AuditableEntity, IHasStatus
{
    public DateOnly PeriodStart { get; set; }
    public DateOnly PeriodEnd { get; set; }

    /// <summary>Draft, Awaiting finalization, Sent back or Finalized.</summary>
    public int StatusId { get; set; } = StatusIds.Payroll.Draft;
    public Status Status { get; set; } = null!;

    /// <summary>Created by the daily check of automatic payroll (GFR-07); otherwise by the user in CreatedBy.</summary>
    public bool CreatedBySystem { get; set; }

    public DateTimeOffset? SentAt { get; set; }
    public Guid? SentByAccountId { get; set; }

    public string? SendBackNote { get; set; }
    public DateTimeOffset? SentBackAt { get; set; }
    public Guid? SentBackByAccountId { get; set; }

    public DateTimeOffset? FinalizedAt { get; set; }
    public Guid? FinalizedByAccountId { get; set; }

    /// <summary>The records of the period can no longer change (BR-LABOR-13).</summary>
    public bool IsLocked => StatusId is StatusIds.Payroll.AwaitingFinalization or StatusIds.Payroll.Finalized;
}
