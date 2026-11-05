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
/// Checks a measurement against its own thresholds (BR-WATER-04) and sends the mandatory water notifications, the alert
/// (MSG-WATER-03) and its withdrawal (MSG-WATER-04), to every active Cooperative Owner and the Zone Managers of the zone,
/// except the Owner who did the action. Notification rows join the caller's unit of work; push is sent after its commit.
/// </summary>
public sealed class WaterAlertService(
    IZoneRecipientQuery recipients, OwnerActorRecipientFilter ownerActorFilter, INotificationDispatcher notifications, TimeProvider timeProvider)
{
    public const string AlertTitle = "Water quality alert";
    public const string WithdrawnTitle = "Water alert withdrawn";

    /// <summary>What the measurement's current values give against the thresholds of the measurement; changes nothing.</summary>
    public static ThresholdEvaluation Evaluate(WaterMeasurement measurement) =>
        WaterThresholdEvaluator.Evaluate(measurement.Ph, measurement.Salinity, measurement.Temperature, WaterThresholds.UsedBy(measurement));

    /// <summary>
    /// Stores the result of <see cref="Evaluate"/> on the measurement. <see cref="WaterMeasurement.AlertedAt"/> says when its
    /// last alert was sent: it is cleared here when no value is outside and set only by <see cref="SendAlertAsync"/>.
    /// </summary>
    public static ThresholdEvaluation Recheck(WaterMeasurement measurement)
    {
        var evaluation = Evaluate(measurement);
        measurement.ThresholdExceeded = evaluation.Exceeded;
        measurement.ViolatedValues = evaluation.ViolatedValues;
        if (!evaluation.Exceeded)
        {
            measurement.AlertedAt = null;
        }

        return evaluation;
    }

    /// <summary>MSG-WATER-03 for a measurement whose <see cref="Recheck"/> found values outside.</summary>
    /// <param name="corrected">An alert after a correction is marked as such in the message.</param>
    public async Task SendAlertAsync(
        WaterMeasurement measurement, ThresholdEvaluation evaluation, string zoneName, string raftCode, Guid actorId, bool corrected, CancellationToken cancellationToken)
    {
        measurement.AlertedAt = timeProvider.GetUtcNow();
        await NotifyAsync(
            measurement,
            NotificationTypes.WaterAlert,
            AlertTitle,
            UserMessages.WaterAlert(corrected, zoneName, raftCode, measurement.MeasuredAt, evaluation.ValuesOutside),
            actorId,
            cancellationToken);
    }

    /// <summary>MSG-WATER-04. Default until the PM answers: mandatory like the alert (<see cref="NotificationTypes"/>).</summary>
    public Task SendWithdrawnAsync(WaterMeasurement measurement, UserMessage message, Guid actorId, CancellationToken cancellationToken) =>
        NotifyAsync(measurement, NotificationTypes.WaterAlertWithdrawn, WithdrawnTitle, message, actorId, cancellationToken);

    private async Task NotifyAsync(
        WaterMeasurement measurement, string type, string title, UserMessage message, Guid actorId, CancellationToken cancellationToken)
    {
        var allRecipients = await recipients.GetRecipientsAsync(measurement.ZoneId, PermissionFunctions.WaterMeasurements, PermissionAction.View, cancellationToken);
        // The Owner is not told of their own action (PM decision 03/11); the Zone Managers still are.
        var accountIds = await ownerActorFilter.WithoutOwnerActorAsync(allRecipients, actorId, cancellationToken);
        await notifications.NotifyAsync(
            accountIds,
            new NotificationContent(type, title, message, new RelatedRecord(RelatedRecordTypes.WaterMeasurement, measurement.Id)),
            cancellationToken);
    }
}
