using OMTS.Domain.Common;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;

namespace OMTS.Domain.Entities.Labor;

/// <summary>
/// E43 – One wage/salary line before aggregation: daily wage, monthly salary, task contract,
/// addition or deduction. Amounts are multiples of 1,000 VND.
/// </summary>
public class PayLine : AuditableEntity
{
    public Guid? EmployeeId { get; set; }
    public Employee? Employee { get; set; }

    public Guid? WorkerId { get; set; }
    public SeasonalWorker? Worker { get; set; }

    public Guid? AttendanceId { get; set; }
    public AttendanceRecord? Attendance { get; set; }

    /// <summary>Contracted task whose acceptance passed.</summary>
    public Guid? AssignmentId { get; set; }
    public WorkAssignment? Assignment { get; set; }

    /// <summary>Empty for task contract lines.</summary>
    public Guid? PayRuleVersionId { get; set; }
    public PayRuleVersion? PayRuleVersion { get; set; }

    /// <summary>Work day for daily wages; first day of the month for monthly salary.</summary>
    public DateOnly PayDate { get; set; }

    public PayLineType LineType { get; set; }
    public decimal Amount { get; set; }

    /// <summary>Required for additions and deductions.</summary>
    public string? Reason { get; set; }

    /// <summary>Empty until the line is added to a settlement sheet.</summary>
    public Guid? SettlementId { get; set; }
    public LaborSettlement? Settlement { get; set; }
}
