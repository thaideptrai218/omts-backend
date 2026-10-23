using OMTS.Domain.Common;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;

namespace OMTS.Domain.Entities.Labor;

/// <summary>
/// E43 – One line of a payslip. System lines are calculated from attendance, confirmed piecework, pay rates and
/// advances and are replaced on refresh; manual lines (Addition/Deduction with a reason) are kept. Amounts are
/// positive multiples of 1,000 VND; the line type says whether it adds or deducts.
/// </summary>
public class PayLine : AuditableEntity
{
    public Guid? EmployeeId { get; set; }
    public Employee? Employee { get; set; }

    public Guid? WorkerId { get; set; }
    public SeasonalWorker? Worker { get; set; }

    public Guid? AttendanceId { get; set; }
    public AttendanceRecord? Attendance { get; set; }

    /// <summary>Confirmed piecework assignment of a piecework line.</summary>
    public Guid? AssignmentId { get; set; }
    public WorkAssignment? Assignment { get; set; }

    /// <summary>Pay rate a line was calculated from; empty for piecework, advance and manual lines.</summary>
    public Guid? PayRuleVersionId { get; set; }
    public PayRuleVersion? PayRuleVersion { get; set; }

    /// <summary>Work day for daily wages, completion date for piecework, last day of the month otherwise.</summary>
    public DateOnly PayDate { get; set; }

    public PayLineType LineType { get; set; }

    /// <summary>What the line is, e.g. the allowance name or the piecework task.</summary>
    public string? Description { get; set; }

    /// <summary>Days, pieces or 1; with <see cref="UnitRate"/> it explains the amount.</summary>
    public decimal? Quantity { get; set; }
    public decimal? UnitRate { get; set; }
    public decimal Amount { get; set; }

    /// <summary>Entered by the Accountant (kept on refresh); required reason.</summary>
    public bool IsManual { get; set; }
    public string? Reason { get; set; }

    public Guid? SettlementId { get; set; }
    public LaborSettlement? Settlement { get; set; }
}
