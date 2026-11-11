using System.Globalization;
using System.Text.RegularExpressions;
using OMTS.Domain.Constants;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Labor.Payrolls;

/// <summary>The rules and names of payroll sheets that the services and readers share (BR-LABOR-10, PAYROLL_API_CONTRACT).</summary>
public static partial class PayrollRules
{
    public const int MaxEmployees = 500;
    public const int MaxCustomDays = 366;
    public const int MaxNoteLength = 500;

    [GeneratedRegex("^\\d{4}-(0[1-9]|1[0-2])$")]
    private static partial Regex ExpenseMonthPattern();

    /// <summary>"Bảng lương tháng 10/2026" for a calendar month, "Bảng lương 01/10/2026 - 15/10/2026" for any other period.</summary>
    public static string NameOf(PayrollPeriodType type, DateOnly from, DateOnly to) => type == PayrollPeriodType.Monthly
        ? $"Bảng lương tháng {to.ToString("MM/yyyy", CultureInfo.InvariantCulture)}"
        : $"Bảng lương {from.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)} - {to.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)}";

    public static PayrollSheetStatus StatusOf(int statusId) => statusId switch
    {
        StatusIds.Payroll.Creating => PayrollSheetStatus.Creating,
        StatusIds.Payroll.Finalized => PayrollSheetStatus.Finalized,
        StatusIds.Payroll.Cancelled => PayrollSheetStatus.Cancelled,
        _ => PayrollSheetStatus.Provisional,
    };

    /// <summary>The catalog statuses behind the contract statuses: Provisional covers the Draft and the SRS v8 states no endpoint reaches.</summary>
    public static IReadOnlyList<int> StatusIdsOf(IEnumerable<PayrollSheetStatus> statuses) => statuses.Distinct().SelectMany(s => s switch
    {
        PayrollSheetStatus.Creating => new[] { StatusIds.Payroll.Creating },
        PayrollSheetStatus.Finalized => new[] { StatusIds.Payroll.Finalized },
        PayrollSheetStatus.Cancelled => new[] { StatusIds.Payroll.Cancelled },
        _ => new[] { StatusIds.Payroll.Draft, StatusIds.Payroll.AwaitingFinalization, StatusIds.Payroll.SentBack },
    }).ToList();

    /// <summary>A Monthly period is a whole calendar month; a Custom one is at most a year (the web sends the month whatever the pay period start day).</summary>
    public static bool IsValidPeriod(PayrollPeriodType type, DateOnly from, DateOnly to) => from <= to && type switch
    {
        PayrollPeriodType.Monthly => from.Day == 1 && to == new DateOnly(from.Year, from.Month, DateTime.DaysInMonth(from.Year, from.Month)),
        _ => to.DayNumber - from.DayNumber < MaxCustomDays,
    };

    public static bool IsExpenseMonth(string? text) => text is not null && ExpenseMonthPattern().IsMatch(text);

    /// <summary>"BL000012" or "PL000345": the prefix and at least six digits.</summary>
    public static string CodeOf(string prefix, long number) => $"{prefix}{number.ToString("000000", CultureInfo.InvariantCulture)}";

    /// <summary>A file name made of letters, digits, dot, dash and underscore only.</summary>
    public static string SafeFileName(string text) => Regex.Replace(text, "[^A-Za-z0-9._-]", "-");
}
