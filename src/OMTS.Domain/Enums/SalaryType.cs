namespace OMTS.Domain.Enums;

/// <summary>
/// How an employee's main pay is worked out (owner decision of 27/11/2026, after the KiotViet "Thiết lập lương" tab). Pay per working day
/// and per session come later.
/// </summary>
public enum SalaryType
{
    /// <summary>A fixed amount for each month (the default for Staff); never cut by attendance, the Owner enters a deduction instead.</summary>
    FixedMonthly = 1,

    /// <summary>The hours worked times the rate of each job type (the default for a seasonal worker).</summary>
    Hourly = 2,
}
