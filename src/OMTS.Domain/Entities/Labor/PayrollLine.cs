using OMTS.Domain.Common;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;

namespace OMTS.Domain.Entities.Labor;

/// <summary>
/// The payslip of one employee in a payroll sheet (LABOR-12, BR-LABOR-10): the work of each job type at the rate in force on each day (<see cref="Jobs"/>), the main pay
/// (the fixed salary, the hourly pay or the per-shift pay), the shift pay of the field work of a fixed-salary employee, the bonus and the manual deduction (sums of the
/// <see cref="Adjustments"/> the Owner enters for a Staff employee) and the advance deduction (the advances taken off when the sheet is finalized). What is paid comes from
/// the wage payments of the line, so it is never stored here.
/// </summary>
public class PayrollLine : AuditableEntity
{
    public Guid PayrollId { get; set; }
    public Payroll Payroll { get; set; } = null!;

    /// <summary>PL000001: one sequence per cooperative.</summary>
    public string Code { get; set; } = string.Empty;

    public Guid EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;

    /// <summary>The salary type in force on the last day of the period; null while the employee has none ("Chưa thiết lập lương").</summary>
    public SalaryType? SalaryType { get; set; }

    /// <summary>Hours worked in the period that were paid by the hour or by the shift, to two decimals.</summary>
    public decimal Hours { get; set; }

    /// <summary>Shifts counted in the period (per-shift pay and field shifts together), in steps of half a shift.</summary>
    public decimal Shifts { get; set; }

    /// <summary>The fixed monthly amount, the hourly pay and the per-shift pay, in whole VND.</summary>
    public decimal MainPay { get; set; }

    /// <summary>The shift pay of the days a fixed-salary employee had a work assignment, in whole VND.</summary>
    public decimal ShiftPay { get; set; }

    /// <summary>The sum of the + adjustments.</summary>
    public decimal Bonus { get; set; }

    /// <summary>The sum of the − adjustments.</summary>
    public decimal ManualDeduction { get; set; }

    /// <summary>Advances taken off this payslip: a preview while the sheet is Provisional, final once it is Finalized.</summary>
    public decimal Deduction { get; set; }

    /// <summary>The rates in force, e.g. "30.000 / 300.000 đ/ca"; null when there are none.</summary>
    public string? HourlyRateNote { get; set; }

    /// <summary>Everything earned less the manual deduction, before the advances.</summary>
    public decimal Income => MainPay + ShiftPay + Bonus - ManualDeduction;

    public decimal Net => Income - Deduction;

    public ICollection<PayrollLineJob> Jobs { get; set; } = [];

    public ICollection<PayrollLineAdjustment> Adjustments { get; set; } = [];
}

/// <summary>The work of one job type at one rate in a payslip, with the name of the job type as it was then.</summary>
public class PayrollLineJob : Entity
{
    public Guid PayrollLineId { get; set; }
    public PayrollLine PayrollLine { get; set; } = null!;

    public Guid JobTypeId { get; set; }
    public string JobName { get; set; } = string.Empty;

    public PayLineKind Kind { get; set; } = PayLineKind.Hourly;

    /// <summary>The rate of an <see cref="PayLineKind.Hourly"/> row; 0 for the shift kinds.</summary>
    public decimal HourlyRate { get; set; }

    /// <summary>The rate of a shift kind row; 0 for an hourly row.</summary>
    public decimal ShiftRate { get; set; }

    /// <summary>Hours worked, also on a shift row (for reading).</summary>
    public decimal Hours { get; set; }

    /// <summary>Shifts counted; 0 for an hourly row.</summary>
    public decimal Shifts { get; set; }

    public decimal Amount { get; set; }
}

/// <summary>A + or − line the Owner adds to the payslip of a Staff employee while the sheet is Provisional: a whole amount above zero and the reason for it.</summary>
public class PayrollLineAdjustment : AuditableEntity
{
    public const int MaxReasonLength = 200;

    public Guid PayrollLineId { get; set; }
    public PayrollLine PayrollLine { get; set; } = null!;

    public PayrollAdjustmentKind Kind { get; set; }
    public decimal Amount { get; set; }
    public string Reason { get; set; } = string.Empty;

    /// <summary>The order the Owner entered the lines in.</summary>
    public int Position { get; set; }
}
