using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Domain.Entities.Cultivation;

namespace OMTS.Application.Features.WaterQuality;

/// <summary>
/// WATER-02.2: rechecks a corrected or voided measurement and sends what <see cref="WaterCorrectionNoticeRule"/> says.
/// Call both methods before the change is saved: they read the other measurements of the zone as stored.
/// </summary>
public sealed class WaterCorrectionNotifier(IWaterMeasurementRepository measurements, WaterAlertService alerts)
{
    private const string CorrectedWord = "corrected";
    private const string VoidedWord = "voided";

    /// <summary>Stores the new result of the corrected values; a correction never changes the thresholds (BR-WATER-04).</summary>
    /// <param name="measurement">With the corrected values, time and raft, tracked, with its zone.</param>
    /// <param name="outsideBefore">The values outside before the correction.</param>
    /// <param name="measuredAtBefore">The time before the correction, which the earlier alert named.</param>
    public async Task AfterCorrectionAsync(
        WaterMeasurement measurement, IReadOnlyCollection<WaterValueOutside> outsideBefore, DateTimeOffset measuredAtBefore, string reason, Guid actorId, CancellationToken cancellationToken)
    {
        var evaluation = WaterAlertService.Recheck(measurement);
        var latest = await measurements.ListLatestActiveAsync(measurement.ZoneId, 2, cancellationToken);
        switch (WaterCorrectionNoticeRule.AfterCorrection(WaterCorrectionNoticeRule.IsLatest(measurement, latest), outsideBefore, evaluation.ValuesOutside))
        {
            case WaterCorrectionNotice.CorrectedAlert:
                await alerts.SendAlertAsync(measurement, evaluation, measurement.Zone.Name, measurement.Raft.Code, actorId, corrected: true, cancellationToken);
                break;
            case WaterCorrectionNotice.AlertWithdrawn:
                await SendWithdrawnAsync(measurement, measuredAtBefore, CorrectedWord, reason, actorId, cancellationToken);
                break;
        }
    }

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
