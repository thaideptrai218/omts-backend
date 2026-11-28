using OMTS.Domain.Common;

namespace OMTS.Domain.Entities.Labor;

/// <summary>
/// People hired for the day of a work-day plan who are not in the system: a number of people paid a rate per session, outside payroll. They count towards the people needed. Paying the line makes a cash
/// voucher of People times the rate; its cost goes to the raft of the plan, or to the zone for a nursery plan.
/// </summary>
public class PlanOutsiderHire : AuditableEntity
{
    public Guid PlanId { get; set; }
    public WorkDayPlan Plan { get; set; } = null!;

    public int People { get; set; }

    /// <summary>VND per person per session.</summary>
    public decimal RatePerSession { get; set; }

    public string? PayeeName { get; set; }
    public string? Note { get; set; }

    public Guid? CashVoucherId { get; set; }
    public DateTimeOffset? PaidAt { get; set; }
    public Guid? PaidByAccountId { get; set; }

    public decimal Amount => People * RatePerSession;
    public bool IsPaid => CashVoucherId is not null;
}
