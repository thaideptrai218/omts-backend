using OMTS.Application.Common;
using OMTS.Domain.Entities.Cultivation;

namespace OMTS.Application.Features.WaterQuality;

/// <summary>
/// Overdue water measurement reminder rule (formerly JOB-05, BR-06). A zone's overdue period starts at its
/// latest Confirmed measurement (or at the zone's creation when it has none); the zone is overdue when that
/// local date is more than one calendar month before today. One reminder per period: a reminder sent after
/// the period started covers it; a newer measurement starts a new period once it is confirmed after that
/// reminder, even when it was measured earlier (offline device synced late).
/// <para>
/// Corrections and voids (ENV-02, OMTS-214) re-arm the reminder per zone and measured day. A correction whose measured
/// day is on or before the day of an earlier version of its chain (pH, salinity or time fixed, or moved back) is the
/// same measurement as that version: it keeps its reminder state and never causes a second reminder for a lapse
/// already reminded. A correction to a day later than every earlier version counts like a newer measurement confirmed
/// at the correction. Voiding the measurement that answered the last reminder, or correcting it to a day before the
/// zone's other measurements, leaves the zone without a valid measurement for that period again, so the reminder is
/// re-armed (<see cref="WaterOverdueRearm"/>).
/// </para>
/// </summary>
public static class WaterOverdueRule
{
    /// <summary>Measured on 23/09 → still on time on 23/10, overdue from 24/10.</summary>
    public static bool IsOverdue(DateOnly periodStartDate, DateOnly today) => periodStartDate.AddMonths(1) < today;

    /// <param name="periodStartConfirmedAt">
    /// When the measurement that starts the period became known (<see cref="PeriodKnownAt"/>; null for a never-measured zone).
    /// </param>
    public static bool NeedsReminder(DateTimeOffset periodStart, DateTimeOffset? remindedAt, DateOnly today, DateTimeOffset? periodStartConfirmedAt = null) =>
        IsOverdue(BusinessClock.ToBusinessDate(periodStart), today)
        && (remindedAt is null || remindedAt <= periodStart || periodStartConfirmedAt > remindedAt);

    /// <summary>
    /// When a version of a correction chain (oldest version first) became known to the reminder: the confirmation of
    /// the earliest version whose measured business day is on or after this version's day, which is the version itself
    /// when it moved the measurement later than every earlier version. 08-20 → 08-10 → 08-15 is known since 08-20's
    /// confirmation; 08-20 → 08-25 → 08-22 since 08-25's.
    /// </summary>
    /// <param name="upTo">The version, by default the last one; later versions (saved after it was read) are ignored.</param>
    public static DateTimeOffset? PeriodKnownAt(IReadOnlyList<WaterMeasurement> chain, Guid? upTo = null)
    {
        var versions = upTo is { } id ? chain.TakeWhile(v => v.Id != id).Concat(chain.Where(v => v.Id == id).Take(1)).ToList() : chain;
        if (versions.Count == 0)
        {
            return null;
        }

        var day = BusinessClock.ToBusinessDate(versions[^1].MeasuredAt);
        return versions.First(v => BusinessClock.ToBusinessDate(v.MeasuredAt) >= day).ConfirmedAt;
    }
}
