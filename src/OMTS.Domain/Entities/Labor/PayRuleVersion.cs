using OMTS.Domain.Common;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Enums;

namespace OMTS.Domain.Entities.Labor;

/// <summary>
/// E42 – Monthly salary (per employee) or daily wage rate (per subject type) valid for a period. Only
/// Approved versions are used; a new version never recalculates finalized periods.
/// </summary>
public class PayRuleVersion : AuditableEntity, IHasStatus
{
    public PaySubjectType SubjectType { get; set; }

    /// <summary>Required for a monthly salary.</summary>
    public Guid? EmployeeId { get; set; }
    public Employee? Employee { get; set; }

    public decimal? MonthlySalary { get; set; }
    public decimal? DailyWageRate { get; set; }

    public DateOnly ValidFrom { get; set; }
    public DateOnly? ValidTo { get; set; }

    public Guid EnteredByAccountId { get; set; }
    public Guid? ApprovedByAccountId { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }

    public int StatusId { get; set; } = StatusIds.PayRule.Draft;
    public Status Status { get; set; } = null!;
}
