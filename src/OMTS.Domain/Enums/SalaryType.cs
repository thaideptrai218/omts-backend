namespace OMTS.Domain.Enums;

/// <summary>
/// How an employee's main pay is worked out (owner decisions of 27/11/2026, after the KiotViet "Thiết lập lương" tab).
/// </summary>
public enum SalaryType
{
    /// <summary>
    /// A fixed amount for each month (the default for Staff); never cut by attendance, the Owner enters a deduction instead. It may also pay the shifts of the field work the employee
    /// is assigned to (<see cref="Entities.Labor.EmployeeSalarySetup.PayFieldShifts"/>).
    /// </summary>
    FixedMonthly = 1,

    /// <summary>The hours worked times the rate of each job type.</summary>
    Hourly = 2,

    /// <summary>The shifts worked (a shift is a set length of actual work, 3 hours by default) times the shift rate of each job type (the default for a seasonal worker).</summary>
    PerShift = 3,
}
