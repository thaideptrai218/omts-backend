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
/// Corrections and voids (ENV-02, OMTS-214) re-arm the reminder per zone and measured day. A correction that keeps the
/// measured day (pH, salinity or time fixed) or moves it to an earlier day is the same measurement: it keeps the
/// reminder state of the version it corrects and never causes a second reminder for a lapse already reminded. A
/// correction that moves the measurement to a later day counts like a newer measurement confirmed at the correction.
/// Voiding the measurement that answered the last reminder leaves the zone without a valid measurement for that
/// period again, so the reminder is re-armed (<see cref="WaterOverdueRearm"/>).
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
    /// When the measurement of a correction chain (oldest version first, ending with the version that starts the
    /// zone's period) became known to the reminder: the confirmation of the first version, replaced by the
    /// confirmation of a correction only when that correction measured a later business day than the version it corrects.
    /// </summary>
    public static DateTimeOffset? PeriodKnownAt(IReadOnlyList<WaterMeasurement> chain)
    {
        DateTimeOffset? knownAt = null;
        WaterMeasurement? previous = null;
        foreach (var version in chain)
        {
            if (previous is null || BusinessClock.ToBusinessDate(version.MeasuredAt) > BusinessClock.ToBusinessDate(previous.MeasuredAt))
            {
                knownAt = version.ConfirmedAt;
            }

            previous = version;
        }

        return knownAt;
    }
}
