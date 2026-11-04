using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;

namespace OMTS.Application.Features.WaterQuality;

/// <summary>
/// Interim until OMTS-219 W3 deletes this class (GFR-04 recomputes the base date on every run). WATER-02 → overdue
/// reminder: a void of the zone's latest Active measurement, or a correction that moves it to a day before the zone's
/// other measurements, removes it as the zone's latest. When it was saved after the last reminder (it answered that
/// reminder) this re-opens the overdue period the reminder was about, so the zone's marker is cleared and the next run
/// reminds again if the zone is overdue. Anything else keeps the marker.
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
        if (measurement.StatusId != StatusIds.WaterMeasurement.Active || zone.WaterOverdueRemindedAt is not { } remindedAt)
        {
            return;
        }

        var latest = await measurements.ListLatestActiveAsync(measurement.ZoneId, 2, cancellationToken);
        if (latest.Count == 0 || latest[0].Id != measurement.Id)
        {
            return;
        }

        // The corrected measurement stays the zone's latest unless it is measured before the next one.
        if (correctedMeasuredAt is { } at && (latest.Count == 1 || at >= latest[1].MeasuredAt))
        {
            return;
        }

        if (measurement.CreatedAt > remindedAt)
        {
            zone.WaterOverdueRemindedAt = null;
        }
    }
}
