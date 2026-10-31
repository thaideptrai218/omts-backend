using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Notifications;
using OMTS.Application.Features.WaterQuality.Dtos;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Application.Features.WaterQuality;

/// <summary>
/// Confirmation step shared by ENV-01 (confirm, create confirmed) and ENV-02 (correction version): stores the
/// threshold set used (BR-07), evaluates the values and, on a violation, raises the alert with mandatory
/// WATER_ALERT notifications (MSG-WATER-03) to every active Cooperative Owner and the zone's staff holding View on
/// WATER_MEASUREMENTS. Notification rows join the caller's unit of work; push is sent after its commit.
/// </summary>
public sealed class WaterAlertService(IZoneRecipientQuery recipients, INotificationDispatcher notifications, TimeProvider timeProvider)
{
    public const string AlertTitle = "Water quality alert";

    /// <summary>{correction_note} of MSG-WATER-03 for a correction version (ENV-02).</summary>
    public const string CorrectionNote = "(corrected measurement)";

    /// <summary>{raft_code} while measurements are recorded per zone, without a raft.</summary>
    public const string NoRaft = "–";

    /// <summary>The measurement must carry its final pH and salinity.</summary>
    public async Task ConfirmAsync(
        WaterMeasurement measurement, FarmingZone zone, WaterThresholds thresholds, Guid? confirmedBy, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        measurement.StatusId = StatusIds.WaterMeasurement.Confirmed;
        measurement.ConfirmedByAccountId = confirmedBy;
        measurement.ConfirmedAt = now;
        measurement.PhMinUsed = thresholds.PhMin;
        measurement.PhMaxUsed = thresholds.PhMax;
        measurement.SalinityMinUsed = thresholds.SalinityMin;
        measurement.SalinityMaxUsed = thresholds.SalinityMax;

        var evaluation = WaterThresholdEvaluator.Evaluate(
            measurement.Ph ?? throw new InvalidOperationException("A confirmed measurement needs its pH."),
            measurement.Salinity ?? throw new InvalidOperationException("A confirmed measurement needs its salinity."),
            thresholds);
        measurement.ThresholdExceeded = evaluation.Exceeded;
        measurement.ViolatedValues = evaluation.ViolatedValues;
        measurement.AlertedAt = evaluation.Exceeded ? now : null;
        if (!evaluation.Exceeded)
        {
            return;
        }

        var accountIds = await recipients.GetRecipientsAsync(zone.Id, PermissionFunctions.WaterMeasurements, PermissionAction.View, cancellationToken);
        var message = UserMessages.WaterAlert(
            measurement.CorrectedFromMeasurementId is null ? "" : CorrectionNote,
            zone.Name,
            NoRaft,
            BusinessClock.Format(measurement.MeasuredAt),
            evaluation.ValuesOutside!);
        await notifications.NotifyAsync(
            accountIds,
            new NotificationContent(NotificationTypes.WaterAlert, AlertTitle, message, new RelatedRecord(RelatedRecordTypes.WaterMeasurement, measurement.Id)),
            cancellationToken);
    }
}
