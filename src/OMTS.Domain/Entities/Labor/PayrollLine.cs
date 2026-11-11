using OMTS.Domain.Common;
using OMTS.Domain.Entities.Identity;

namespace OMTS.Domain.Entities.Labor;

/// <summary>
/// The payslip of one employee in a payroll sheet (LABOR-12, BR-LABOR-10): the hours of each job type at the rate in force on each
/// day (<see cref="Jobs"/>), the main pay they give, a bonus (always 0 at this level of payroll) and the deduction (the advances
/// taken off when the sheet is finalized). What is paid comes from the wage payments of the line, so it is never stored here.
/// </summary>
public class PayrollLine : AuditableEntity
{
    public Guid PayrollId { get; set; }
    public Payroll Payroll { get; set; } = null!;

    /// <summary>PL000001: one sequence per cooperative.</summary>
    public string Code { get; set; } = string.Empty;

    public Guid EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;

    /// <summary>Hours worked in the period, to two decimals.</summary>
    public decimal Hours { get; set; }

    /// <summary>Hours times the rate of each job type, in whole VND.</summary>
    public decimal MainPay { get; set; }

    public decimal Bonus { get; set; }

    /// <summary>Advances taken off this payslip: a preview while the sheet is Provisional, final once it is Finalized.</summary>
    public decimal Deduction { get; set; }

    /// <summary>The rates in force, e.g. "30.000 / 32.000"; null when there are none.</summary>
    public string? HourlyRateNote { get; set; }

    public decimal Income => MainPay + Bonus;
    public decimal Net => Income - Deduction;

    public ICollection<PayrollLineJob> Jobs { get; set; } = [];
}

/// <summary>The hours of one job type at one hourly rate in a payslip, with the name of the job type as it was then.</summary>
public class PayrollLineJob : Entity
{
    public Guid PayrollLineId { get; set; }
    public PayrollLine PayrollLine { get; set; } = null!;

    public Guid JobTypeId { get; set; }
    public string JobName { get; set; } = string.Empty;
    public decimal HourlyRate { get; set; }
    public decimal Hours { get; set; }
    public decimal Amount { get; set; }
}
