using System.Globalization;

namespace OMTS.Application.Features.Reports;

/// <summary>The unit of a report that cuts a period into days, weeks or months (REPORT-01 grouped by period, REPORT-02, REPORT-03).</summary>
public enum ReportPeriodUnit
{
    Day,
    Week,
    Month,
}

/// <summary>
/// The buckets of the period reports. Dates here are business dates (UTC+7: the invoice date is already the business date of the invoice time, BR-SALE-08), so a week starts on
/// its Monday at 00:00 Vietnam time (ISO 8601) and a sale at 00:30 on Monday is in the new week, whatever the server's time zone.
/// </summary>
public static class ReportPeriodBuckets
{
    /// <summary>The first day of the bucket the date falls in: the day itself, the Monday of its ISO week, or the first of its month.</summary>
    public static DateOnly StartOf(DateOnly date, ReportPeriodUnit unit) => unit switch
    {
        ReportPeriodUnit.Day => date,
        ReportPeriodUnit.Week => date.AddDays(-(((int)date.DayOfWeek + 6) % 7)),
        _ => new DateOnly(date.Year, date.Month, 1),
    };

    /// <summary>The key of a bucket: its first day as yyyy-MM-dd, which sorts in time order and is what a drill-down sends back.</summary>
    public static string Key(DateOnly start) => start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>What people read: the day (2026-11-09), the ISO week (2026-W46; the ISO year, which can differ from the calendar year around New Year) or the month (2026-11).</summary>
    public static string Label(DateOnly start, ReportPeriodUnit unit)
    {
        switch (unit)
        {
            case ReportPeriodUnit.Week:
                var monday = start.ToDateTime(TimeOnly.MinValue);
                return $"{ISOWeek.GetYear(monday)}-W{ISOWeek.GetWeekOfYear(monday):00}";
            case ReportPeriodUnit.Month:
                return start.ToString("yyyy-MM", CultureInfo.InvariantCulture);
            default:
                return Key(start);
        }
    }

    /// <summary>The unit named by a client, in any letter case; null when it is not one of the three (the caller answers MSG-GEN-01).</summary>
    public static ReportPeriodUnit? Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "day" => ReportPeriodUnit.Day,
        "week" => ReportPeriodUnit.Week,
        "month" => ReportPeriodUnit.Month,
        _ => null,
    };
}
