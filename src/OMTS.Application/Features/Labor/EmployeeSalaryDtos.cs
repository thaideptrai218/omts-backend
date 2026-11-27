using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Labor;

/// <param name="Type">FixedMonthly, Hourly or PerShift.</param>
/// <param name="MonthlyAmount">Whole VND above 0 for FixedMonthly; left out for Hourly and PerShift (the hourly or shift rate of each job type applies).</param>
/// <param name="EffectiveFrom">The first day the setup applies. A date that already has a setup replaces it; the other dates stay.</param>
/// <param name="RowVersion">The employee version the screen was read from; an older one answers MSG06.</param>
/// <param name="PayFieldShifts">FixedMonthly only: pay the shifts of the days with a work assignment on top of the salary. Left out means true; it must be left out or false for the other types.</param>
public sealed record ChangeSalaryRequest(SalaryType? Type, decimal? MonthlyAmount, DateOnly? EffectiveFrom, string? RowVersion, bool? PayFieldShifts = null);

/// <param name="PayFieldShifts">True only for a FixedMonthly setup that pays field shifts.</param>
public sealed record SalarySetupView(SalaryType Type, decimal? MonthlyAmount, DateOnly EffectiveFrom, DateTimeOffset EnteredAt, bool PayFieldShifts = false);

/// <summary>The "Thiết lập lương" tab of one employee.</summary>
/// <param name="Current">The setup in force today; null when there is none yet (see <paramref name="CurrentType"/>).</param>
/// <param name="CurrentType">
/// The salary type that applies today: the one of <paramref name="Current"/>, else the default of the employee type (PerShift for a
/// seasonal worker; none for Staff, shown as "Chưa thiết lập lương").
/// </param>
/// <param name="History">Every setup, the latest date first; one dated after today is already listed.</param>
/// <param name="RowVersion">The employee version to send back with a change.</param>
public sealed record EmployeeSalaryView(
    Guid EmployeeId,
    EmployeeType? EmployeeType,
    SalarySetupView? Current,
    SalaryType? CurrentType,
    IReadOnlyList<SalarySetupView> History,
    string RowVersion);
