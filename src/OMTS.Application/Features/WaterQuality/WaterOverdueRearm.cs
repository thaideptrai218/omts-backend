using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;

namespace OMTS.Application.Features.WaterQuality;

/// <summary>
/// ENV-02 → overdue reminder (OMTS-214): a void of the zone's latest Confirmed measurement, or a correction that moves it
/// to a day before the zone's other measurements, removes it as the zone's latest. When it became known after the last
/// reminder (it answered that reminder) this re-opens the overdue period the reminder was about, so the zone's marker
/// is cleared and the next run reminds again if the zone is overdue. Anything else keeps the marker: the zone's latest
/// measurement is unchanged, the reminder was sent while the removed measurement was already the latest one, or the
/// corrected version stays the latest and the reminder rule itself decides (<see cref="WaterOverdueRule.PeriodKnownAt"/>).
/// </summary>
public sealed class WaterOverdueRearm(IWaterMeasurementRepository measurements)
{
    /// <summary>Call before the void of <paramref name="measurement"/> (tracked, with its zone) is saved; the change joins that save.</summary>
    public Task BeforeVoidAsync(WaterMeasurement measurement, CancellationToken cancellationToken) =>
        ReArmWhenTheAnswerIsRemovedAsync(measurement, correctedMeasuredAt: null, cancellationToken);

    /// <summary>Call before the correction of <paramref name="measurement"/> to <paramref name="correctedMeasuredAt"/> is saved.</summary>
    public Task BeforeCorrectionAsync(WaterMeasurement measurement, DateTimeOffset correctedMeasuredAt, CancellationToken cancellationToken) =>
        ReArmWhenTheAnswerIsRemovedAsync(measurement, correctedMeasuredAt, cancellationToken);

    private async Task ReArmWhenTheAnswerIsRemovedAsync(WaterMeasurement measurement, DateTimeOffset? correctedMeasuredAt, CancellationToken cancellationToken)
    {
        var zone = measurement.Zone;
        if (measurement.StatusId != StatusIds.WaterMeasurement.Confirmed || zone.WaterOverdueRemindedAt is not { } remindedAt)
        {
            return;
        }

        var latest = await measurements.ListLatestConfirmedAsync(measurement.ZoneId, 2, cancellationToken);
        if (latest.Count == 0 || latest[0].Id != measurement.Id)
        {
            return;
        }

        // The new version stays the zone's latest unless it is measured before the next one (a tie goes to the newer confirmation).
        if (correctedMeasuredAt is { } at && (latest.Count == 1 || at >= latest[1].MeasuredAt))
        {
            return;
        }

        var chain = await measurements.GetVersionChainAsync(measurement.Id, cancellationToken);
        if (WaterOverdueRule.PeriodKnownAt(chain, upTo: measurement.Id) > remindedAt)
        {
            zone.WaterOverdueRemindedAt = null;
        }
    }
}
