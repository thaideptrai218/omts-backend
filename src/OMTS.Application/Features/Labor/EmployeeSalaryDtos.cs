using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Labor;

/// <param name="Type">FixedMonthly or Hourly.</param>
/// <param name="MonthlyAmount">Whole VND above 0 for FixedMonthly; left out for Hourly (the rate of each job type applies).</param>
/// <param name="EffectiveFrom">The first day the setup applies. A date that already has a setup replaces it; the other dates stay.</param>
/// <param name="RowVersion">The employee version the screen was read from; an older one answers MSG06.</param>
public sealed record ChangeSalaryRequest(SalaryType? Type, decimal? MonthlyAmount, DateOnly? EffectiveFrom, string? RowVersion);

public sealed record SalarySetupView(SalaryType Type, decimal? MonthlyAmount, DateOnly EffectiveFrom, DateTimeOffset EnteredAt);

/// <summary>The "Thiết lập lương" tab of one employee.</summary>
/// <param name="Current">The setup in force today; null when there is none yet (see <paramref name="CurrentType"/>).</param>
/// <param name="CurrentType">
/// The salary type that applies today: the one of <paramref name="Current"/>, else the default of the employee type (Hourly for a
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
