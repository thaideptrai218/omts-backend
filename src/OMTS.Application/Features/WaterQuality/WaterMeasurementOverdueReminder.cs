using OMTS.Application.Common.Interfaces.Messaging;
using OMTS.Application.Common.Models.Persistence;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Application.Features.WaterQuality;

/// <summary>
/// Daily overdue water measurement reminder (formerly JOB-05, ENV-01 time event): every In Use zone whose
/// latest Confirmed measurement is more than one calendar month old (or that has none and was created more
/// than a month ago) gets MSG26 once per overdue period, in-app and by push, to every active Cooperative Owner
/// and the zone's staff holding View on WATER_MEASUREMENTS. Drafts, corrected and voided records never count; how a
/// correction or a void re-arms the reminder is described on <see cref="WaterOverdueRule"/>.
/// </summary>
public sealed class WaterMeasurementOverdueReminder(
    IWaterMeasurementRepository measurements,
    IZoneRecipientQuery recipients,
    INotificationDispatcher notifications,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    public const string Title = "Water measurement overdue";

    /// <summary>Sends the reminders due on <paramref name="today"/> (business date); returns how many zones were reminded.</summary>
    public async Task<int> RunOnceAsync(DateOnly today, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var reminded = 0;
        foreach (var latest in await measurements.ListInUseZonesWithLatestConfirmedAsync(cancellationToken))
        {
            var zone = latest.Zone;
            var knownAt = await PeriodKnownAtAsync(latest, cancellationToken);
            if (!WaterOverdueRule.NeedsReminder(latest.LatestConfirmedMeasuredAt ?? zone.CreatedAt, zone.WaterOverdueRemindedAt, today, knownAt))
            {
                continue;
            }

            var accountIds = await recipients.GetRecipientsAsync(zone.Id, PermissionFunctions.WaterMeasurements, PermissionAction.View, cancellationToken);
            await notifications.NotifyAsync(
                accountIds, NotificationTypes.WaterMeasurementOverdue, Title, UserMessages.WaterMeasurementOverdue(zone.Name).Text,
                nameof(FarmingZone), zone.Code, isMandatory: false, cancellationToken);
            zone.WaterOverdueRemindedAt = now;
            reminded++;
        }

        if (reminded > 0)
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return reminded;
    }

    // A correction (ENV-02) is confirmed now but keeps the reminder state of an earlier version unless it moved the
    // measurement later than all of them (WaterOverdueRule.PeriodKnownAt); only read when a reminder was already sent.
    // The chain stops at the version read above, so a correction saved in between is not mixed in.
    private async Task<DateTimeOffset?> PeriodKnownAtAsync(ZoneLatestWaterMeasurement latest, CancellationToken cancellationToken) =>
        latest is { LatestIsCorrection: true, LatestId: { } id, Zone.WaterOverdueRemindedAt: not null }
            ? WaterOverdueRule.PeriodKnownAt(await measurements.GetVersionChainAsync(id, cancellationToken), upTo: id)
            : latest.LatestConfirmedAt;
}
