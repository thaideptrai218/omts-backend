using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;

namespace OMTS.Application.Features.WaterQuality;

/// <summary>
/// ENV-02 void → overdue reminder (OMTS-214): voiding the zone's latest Confirmed measurement when it became known after
/// the last reminder (it answered that reminder) re-opens the overdue period the reminder was about, so the zone's
/// marker is cleared and the next run reminds again if the zone is overdue. Any other void keeps the marker: the zone's
/// period is unchanged, or the reminder was already sent while the voided measurement was the latest one.
/// </summary>
public sealed class WaterOverdueRearm(IWaterMeasurementRepository measurements)
{
    /// <summary>Call before the void of <paramref name="measurement"/> (tracked, with its zone) is saved; the change joins that save.</summary>
    public async Task BeforeVoidAsync(WaterMeasurement measurement, CancellationToken cancellationToken)
    {
        var zone = measurement.Zone;
        if (measurement.StatusId != StatusIds.WaterMeasurement.Confirmed || zone.WaterOverdueRemindedAt is not { } remindedAt)
        {
            return;
        }

        var latest = await measurements.GetLatestConfirmedAsync(measurement.ZoneId, cancellationToken);
        if (latest?.Id != measurement.Id)
        {
            return;
        }

        var chain = await measurements.GetVersionChainAsync(measurement.Id, cancellationToken);
        if (WaterOverdueRule.PeriodKnownAt(chain) > remindedAt)
        {
            zone.WaterOverdueRemindedAt = null;
        }
    }
}
