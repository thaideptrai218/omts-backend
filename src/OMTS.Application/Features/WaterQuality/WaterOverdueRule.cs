using OMTS.Application.Common;

namespace OMTS.Application.Features.WaterQuality;

/// <summary>
/// GFR-04, worked out from the data on every run. The base date is the later of the latest active measurement and the
/// earliest Open cycle start of the zone, so a new measurement, a correction or a void moves it with nothing to reset. A
/// zone is overdue when the base date is more than one calendar month before today, and is reminded again every month.
/// </summary>
public static class WaterOverdueRule
{
    /// <param name="latestMeasuredAt">The latest active measurement of the zone; null when it has none.</param>
    public static DateOnly BaseDate(DateTimeOffset? latestMeasuredAt, DateOnly earliestOpenStart) =>
        latestMeasuredAt is { } measuredAt && BusinessClock.ToBusinessDate(measuredAt) > earliestOpenStart
            ? BusinessClock.ToBusinessDate(measuredAt)
            : earliestOpenStart;

    /// <summary>Base 23/09: on time on 23/10, overdue from 24/10. Reminded on 24/10: next on 24/11.</summary>
    /// <param name="lastSent">The business date of the last reminder of the zone; null when it has had none.</param>
    public static bool IsDue(DateOnly baseDate, DateOnly? lastSent, DateOnly today) =>
        baseDate.AddMonths(1) < today && (lastSent is not { } sent || sent.AddMonths(1) <= today);
}
