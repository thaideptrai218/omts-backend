using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Domain.Entities.Cultivation;

namespace OMTS.Application.Features.WaterQuality;

/// <summary>
/// WATER-02.2: sends what <see cref="WaterCorrectionNoticeRule"/> says after a measurement is voided.
/// Call it before the change is saved: it reads the other measurements of the zone as stored.
/// </summary>
public sealed class WaterCorrectionNotifier(IWaterMeasurementRepository measurements, WaterAlertService alerts)
{
    private const string VoidedWord = "voided";

    /// <param name="measurement">Still with its values and time; a voided measurement keeps them.</param>
    public async Task AfterVoidAsync(WaterMeasurement measurement, string reason, Guid actorId, CancellationToken cancellationToken)
    {
        var latest = await measurements.ListLatestActiveAsync(measurement.ZoneId, 1, cancellationToken);
        var wasLatest = latest.Count > 0 && latest[0].Id == measurement.Id;
        if (WaterCorrectionNoticeRule.AfterVoid(wasLatest, WaterAlertService.Evaluate(measurement).Exceeded) == WaterCorrectionNotice.AlertWithdrawn)
        {
            await SendWithdrawnAsync(measurement, measurement.MeasuredAt, VoidedWord, reason, actorId, cancellationToken);
        }
    }

    private async Task SendWithdrawnAsync(
        WaterMeasurement measurement, DateTimeOffset measuredAt, string correctedOrVoided, string reason, Guid actorId, CancellationToken cancellationToken)
    {
        var names = await measurements.GetAccountNamesAsync([actorId], cancellationToken);
        var message = UserMessages.WaterAlertWithdrawn(measurement.Zone.Name, measuredAt, names.GetValueOrDefault(actorId, "Unknown account"), correctedOrVoided, reason);
        await alerts.SendWithdrawnAsync(measurement, message, actorId, cancellationToken);
    }
}
