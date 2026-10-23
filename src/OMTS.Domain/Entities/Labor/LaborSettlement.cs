using OMTS.Domain.Common;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Platform;

namespace OMTS.Domain.Entities.Labor;

/// <summary>
/// E44 – Payslip: the amount payable to one payee (exactly one of employee, seasonal worker or team) for the
/// month of its payroll. Finalizing is not paying; it becomes Fully Paid when total payments equal the net pay.
/// </summary>
public class LaborSettlement : AuditableEntity, IHasStatus
{
    public Guid PayrollId { get; set; }
    public Payroll Payroll { get; set; } = null!;

    public Guid? ContractorId { get; set; }
    public Contractor? Contractor { get; set; }

    public Guid? EmployeeId { get; set; }
    public Employee? Employee { get; set; }

    public Guid? WorkerId { get; set; }
    public SeasonalWorker? Worker { get; set; }

    public DateOnly PeriodStart { get; set; }
    public DateOnly PeriodEnd { get; set; }

    /// <summary>Net pay in VND (earnings − deductions − advance deduction, never negative), multiple of 1,000.</summary>
    public decimal TotalObligation { get; set; }

    /// <summary>Paid days without a valid pay rate (missingRate); finalization is refused while any exist (MSG50).</summary>
    public List<DateOnly> MissingRateDates { get; set; } = [];

    public Guid PreparedByAccountId { get; set; }
    public Guid? ApprovedByAccountId { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }

    public int StatusId { get; set; } = StatusIds.LaborSettlement.Draft;
    public Status Status { get; set; } = null!;

    public ICollection<PayLine> PayLines { get; set; } = [];
    public ICollection<LaborPayment> Payments { get; set; } = [];
}
