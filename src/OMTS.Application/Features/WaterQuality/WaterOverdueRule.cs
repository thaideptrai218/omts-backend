using OMTS.Application.Common;

namespace OMTS.Application.Features.WaterQuality;

/// <summary>
/// Overdue water measurement reminder rule. Interim until OMTS-219 W3 applies GFR-04 (Open cycles, base date, monthly
/// repeat). A zone's overdue period starts at its latest Active measurement (or at the zone's creation when it has
/// none); the zone is overdue when that local date is more than one calendar month before today. One reminder per
/// period: a reminder covers the period when the period was already overdue at that time, and the measurement starting
/// it was saved before the reminder.
/// </summary>
public static class WaterOverdueRule
{
    /// <summary>Measured on 23/09 → still on time on 23/10, overdue from 24/10.</summary>
    public static bool IsOverdue(DateOnly periodStartDate, DateOnly today) => periodStartDate.AddMonths(1) < today;

    /// <param name="periodStartSavedAt">When the measurement that starts the period was saved (null for a never-measured zone).</param>
    public static bool NeedsReminder(DateTimeOffset periodStart, DateTimeOffset? remindedAt, DateOnly today, DateTimeOffset? periodStartSavedAt = null)
    {
        var start = BusinessClock.ToBusinessDate(periodStart);
        return IsOverdue(start, today)
            && (remindedAt is not { } reminded || periodStartSavedAt > reminded || !IsOverdue(start, BusinessClock.ToBusinessDate(reminded)));
    }
}
