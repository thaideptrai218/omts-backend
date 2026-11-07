using System.Globalization;

namespace OMTS.Application.Features.Labor;

/// <summary>A pay period, named after the month in which it ends: 26/09 to 25/10 is "10/2026" (BR-LABOR-13).</summary>
public sealed record PayPeriod(DateOnly Start, DateOnly End)
{
    public string Name => NameOf(End);

    public static string NameOf(DateOnly end) => end.ToString("MM/yyyy", CultureInfo.InvariantCulture);

    public bool Contains(DateOnly day) => Start <= day && day <= End;

    /// <summary>As shown in messages, e.g. "10/2026 (26/09/2026 to 25/10/2026)".</summary>
    public string Describe() =>
        $"{Name} ({Start.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)} to {End.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)})";
}

/// <summary>
/// The arithmetic of pay periods on business dates (UTC+7, BR-LABOR-13). A start day of at most 28 exists in every month, so
/// adding months never moves it. No stored payroll ever changes: a new start day applies from the first day after the last
/// pay period that has a payroll.
/// </summary>
public static class PayPeriodCalendar
{
    public const int FirstStartDay = 1;
    public const int LastStartDay = 28;

    public static bool IsStartDay(int? day) => day is >= FirstStartDay and <= LastStartDay;

    /// <summary>
    /// The pay period after one that ended on <paramref name="previousEnd"/>: from the next day to the day before the next
    /// start day, so the first period under a new start day is shorter, once: after 31/10 with the start day 26, 01/11 to 25/11.
    /// </summary>
    public static PayPeriod After(DateOnly previousEnd, int startDay)
    {
        var start = previousEnd.AddDays(1);
        var sameMonth = new DateOnly(start.Year, start.Month, startDay);
        var nextStart = sameMonth > start ? sameMonth : sameMonth.AddMonths(1);
        return new PayPeriod(start, nextStart.AddDays(-1));
    }

    /// <summary>The whole month of the start day that holds <paramref name="day"/>, whatever is stored: with the start day 26, 10/10 is in 26/09 to 25/10.</summary>
    public static PayPeriod Aligned(DateOnly day, int startDay)
    {
        var sameMonth = new DateOnly(day.Year, day.Month, startDay);
        var start = sameMonth <= day ? sameMonth : sameMonth.AddMonths(-1);
        return new PayPeriod(start, start.AddMonths(1).AddDays(-1));
    }

    /// <summary>
    /// The pay period of <paramref name="day"/>: the stored one that holds it; else the one reached from the last stored period
    /// before it; else its whole month. A period not stored yet ends before the next stored one.
    /// </summary>
    /// <param name="stored">The periods that have a payroll, in any order; they do not overlap.</param>
    public static PayPeriod Containing(DateOnly day, int startDay, IEnumerable<PayPeriod> stored)
    {
        var periods = stored.OrderBy(p => p.Start).ToList();
        if (periods.FirstOrDefault(p => p.Contains(day)) is { } holding)
        {
            return holding;
        }

        var period = periods.LastOrDefault(p => p.End < day) is { } before ? After(before.End, startDay) : Aligned(day, startDay);
        while (period.End < day)
        {
            period = After(period.End, startDay);
        }

        return periods.FirstOrDefault(p => p.Start > day) is { } next && period.End >= next.Start
            ? period with { End = next.Start.AddDays(-1) }
            : period;
    }

    /// <summary>
    /// The next two pay periods to be created (LABOR-16 step 3): those after the last period that has a payroll, or, when no
    /// payroll exists yet, the period of <paramref name="today"/> and the one after it.
    /// </summary>
    public static IReadOnlyList<PayPeriod> NextTwo(DateOnly? lastPayrollEnd, DateOnly today, int startDay)
    {
        var first = lastPayrollEnd is { } end ? After(end, startDay) : Aligned(today, startDay);
        return [first, After(first.End, startDay)];
    }
}
