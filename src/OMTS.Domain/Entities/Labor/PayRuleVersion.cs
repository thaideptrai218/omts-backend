using OMTS.Domain.Common;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Enums;

namespace OMTS.Domain.Entities.Labor;

/// <summary>
/// E42 – Pay rate of one person (exactly one of EmployeeId/WorkerId) valid for a period (LAB-06). There is no
/// Owner approval: a saved rate applies from <see cref="ValidFrom"/> and is stored as Approved. Versions of a
/// person never overlap; a new version never recalculates finalized payslips.
/// </summary>
public class PayRuleVersion : AuditableEntity, IHasStatus
{
    public Guid? EmployeeId { get; set; }
    public Employee? Employee { get; set; }

    public Guid? WorkerId { get; set; }
    public SeasonalWorker? Worker { get; set; }

    public SalaryType SalaryType { get; set; }

    /// <summary>VND, multiple of 1,000: the monthly salary, or the rate per paid day for <see cref="SalaryType.DailyWage"/>.</summary>
    public decimal Amount { get; set; }

    /// <summary>Required for <see cref="SalaryType.PerWorkingDay"/>: whole number 1–31.</summary>
    public int? StandardWorkingDays { get; set; }

    /// <summary>Allowances and deductions paid with this rate.</summary>
    public List<PayComponent> Components { get; set; } = [];

    public DateOnly ValidFrom { get; set; }
    public DateOnly? ValidTo { get; set; }

    public Guid EnteredByAccountId { get; set; }
    public Guid? ApprovedByAccountId { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }

    public int StatusId { get; set; } = StatusIds.PayRule.Approved;
    public Status Status { get; set; } = null!;

    public bool IsValidOn(DateOnly day) => ValidFrom <= day && (ValidTo is null || day <= ValidTo);
}
