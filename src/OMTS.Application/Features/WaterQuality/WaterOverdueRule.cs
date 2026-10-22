using OMTS.Application.Common;

namespace OMTS.Application.Features.WaterQuality;

/// <summary>
/// Overdue water measurement reminder rule (formerly JOB-05, BR-06). A zone's overdue period starts at its
/// latest Confirmed measurement (or at the zone's creation when it has none); the zone is overdue when that
/// local date is more than one calendar month before today. One reminder per period: a reminder sent after
/// the period started covers it, and only a measurement taken after that reminder starts a new period.
/// </summary>
public static class WaterOverdueRule
{
    /// <summary>Measured on 23/09 → still on time on 23/10, overdue from 24/10.</summary>
    public static bool IsOverdue(DateOnly periodStartDate, DateOnly today) => periodStartDate.AddMonths(1) < today;

    public static bool NeedsReminder(DateTimeOffset periodStart, DateTimeOffset? remindedAt, DateOnly today) =>
        IsOverdue(BusinessClock.ToBusinessDate(periodStart), today) && (remindedAt is null || remindedAt <= periodStart);
}
