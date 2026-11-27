using OMTS.Application.Features.Labor.Work;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Labor.Payrolls;

/// <summary>
/// What the salary setups of an employee say for a period, day by day: the setup in force on a day (the default of the employee type when there is none), and whether
/// the employee is paid, or lacks a setup, without any attendance. The employee type and the setup only change on their own dates, so the days that matter are the first
/// day of the period and the dates inside it.
/// </summary>
public static class SalaryPlan
{
    /// <summary>The salary type that applies on <paramref name="day"/>: the setup in force, else the default of the employee type; null for a Staff employee with no setup.</summary>
    public static SalaryType? TypeOn(Employee employee, DateOnly day) =>
        EmployeeSalarySetup.EffectiveOn(employee.SalarySetups, day)?.Type ?? EmployeeSalarySetup.DefaultFor(WorkRules.TypeOn(employee, day));

    /// <summary>A Staff employee who has no setup on some day of the period: there is no pay to work out ("Chưa thiết lập lương").</summary>
    public static bool IsMissingSetup(Employee employee, DateOnly from, DateOnly to) =>
        ChangeDays(employee, from, to).Any(day => EmployeeSalarySetup.EffectiveOn(employee.SalarySetups, day) is null && WorkRules.TypeOn(employee, day) == EmployeeType.Staff);

    /// <summary>
    /// An employee with no attendance in the period that still belongs on a sheet of everyone: one with a fixed salary on some day of the period, or one who lacks a setup.
    /// The employee must have been employed in the period.
    /// </summary>
    public static bool IsPaidWithoutAttendance(Employee employee, DateOnly from, DateOnly to) =>
        IsEmployedIn(employee, from, to)
        && (IsMissingSetup(employee, from, to) || ChangeDays(employee, from, to).Any(day => TypeOn(employee, day) == SalaryType.FixedMonthly));

    /// <summary>Not started after the period and not left before it (an employee ended on a day is Left from the next day).</summary>
    public static bool IsEmployedIn(Employee employee, DateOnly from, DateOnly to) =>
        (employee.StartDate is not { } start || start <= to)
        && (employee.LastWorkingDay is { } last ? last >= from : employee.StatusId == OMTS.Domain.Constants.StatusIds.Employee.Working);

    private static IEnumerable<DateOnly> ChangeDays(Employee employee, DateOnly from, DateOnly to) =>
        new[] { from }
            .Concat(employee.SalarySetups.Select(s => s.EffectiveFrom))
            .Concat(employee.TypePeriods.Select(t => t.EffectiveFrom))
            .Where(day => day >= from && day <= to)
            .Distinct();
}
