using OMTS.Domain.Common;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;

namespace OMTS.Domain.Entities.Labor;

/// <summary>
/// The salary type of an employee from an effective date, like <see cref="PayRate"/>: a new setup never rewrites the days before its date,
/// so a payroll of an earlier month keeps the setup of that month. One row per employee and date.
/// </summary>
public class EmployeeSalarySetup : AuditableEntity
{
    public Guid EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;

    public SalaryType Type { get; set; }

    /// <summary>Whole VND for each month when <see cref="Type"/> is <see cref="SalaryType.FixedMonthly"/>; null for hourly pay.</summary>
    public decimal? MonthlyAmount { get; set; }

    public DateOnly EffectiveFrom { get; set; }
    public Guid EnteredByAccountId { get; set; }

    /// <summary>The setup in force on <paramref name="day"/>: the latest from that day or before; none for a day before every setup.</summary>
    public static EmployeeSalarySetup? EffectiveOn(IEnumerable<EmployeeSalarySetup> setups, DateOnly day) =>
        setups.Where(s => s.EffectiveFrom <= day).MaxBy(s => s.EffectiveFrom);

    /// <summary>
    /// The salary type when no setup is in force: a seasonal worker is paid by the hour; a Staff employee has none until the Owner enters
    /// the monthly amount ("Chưa thiết lập lương").
    /// </summary>
    public static SalaryType? DefaultFor(EmployeeType? employeeType) => employeeType == EmployeeType.SeasonalWorker ? SalaryType.Hourly : null;
}
