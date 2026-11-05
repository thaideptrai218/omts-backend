using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Notifications;
using OMTS.Application.Features.WaterQuality.Dtos;
using OMTS.Domain.Entities.Cultivation;

namespace OMTS.Application.Features.WaterQuality;

/// <summary>
/// Checks a measurement against its own thresholds (BR-WATER-04) and, when a value is outside, sends the mandatory
/// WATER_ALERT notification (MSG-WATER-03) to every active Cooperative Owner and the Zone Managers of the zone, except
/// the Owner who did the action. Notification rows join the caller's unit of work; push is sent after its commit.
/// </summary>
public sealed class WaterAlertService(
    IZoneRecipientQuery recipients, OwnerActorRecipientFilter ownerActorFilter, INotificationDispatcher notifications, TimeProvider timeProvider)
{
    public const string AlertTitle = "Water quality alert";

    /// <param name="measurement">With its final values and the thresholds of the measurement.</param>
    /// <param name="corrected">An alert after a correction is marked as such in the message.</param>
    public async Task EvaluateAsync(
        WaterMeasurement measurement, string zoneName, string raftCode, Guid actorId, bool corrected, CancellationToken cancellationToken)
    {
        var evaluation = WaterThresholdEvaluator.Evaluate(measurement.Ph, measurement.Salinity, measurement.Temperature, WaterThresholds.UsedBy(measurement));
        measurement.ThresholdExceeded = evaluation.Exceeded;
        measurement.ViolatedValues = evaluation.ViolatedValues;
        measurement.AlertedAt = evaluation.Exceeded ? timeProvider.GetUtcNow() : null;
        if (!evaluation.Exceeded)
        {
            return;
        }

        var allRecipients = await recipients.GetRecipientsAsync(measurement.ZoneId, PermissionFunctions.WaterMeasurements, PermissionAction.View, cancellationToken);
        // The Owner is not told of their own measurement or correction (PM decision 03/11); the Zone Managers still are.
        var accountIds = await ownerActorFilter.WithoutOwnerActorAsync(allRecipients, actorId, cancellationToken);
        var message = UserMessages.WaterAlert(corrected, zoneName, raftCode, measurement.MeasuredAt, evaluation.ValuesOutside);
        await notifications.NotifyAsync(
            accountIds,
            new NotificationContent(NotificationTypes.WaterAlert, AlertTitle, message, new RelatedRecord(RelatedRecordTypes.WaterMeasurement, measurement.Id)),
            cancellationToken);
    }
}
