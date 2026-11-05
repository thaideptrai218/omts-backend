using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Notifications;

namespace OMTS.Application.Features.WaterQuality;

/// <summary>
/// Daily overdue water measurement reminder. Interim until OMTS-219 W3 applies GFR-04: every In Use zone whose latest
/// Active measurement is more than one calendar month old (or that has none and was created more than a month ago) gets
/// MSG-WATER-06 once per overdue period, kept and pushed (normal type), to every active Cooperative Owner and the Zone
/// Managers of the zone. Voided records never count; see <see cref="WaterOverdueRule"/> and <see cref="WaterOverdueRearm"/>.
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
        foreach (var latest in await measurements.ListInUseZonesWithLatestActiveAsync(cancellationToken))
        {
            var zone = latest.Zone;
            if (!WaterOverdueRule.NeedsReminder(latest.LatestMeasuredAt ?? zone.CreatedAt, zone.WaterOverdueRemindedAt, today, latest.LatestSavedAt))
            {
                continue;
            }

            var accountIds = await recipients.GetRecipientsAsync(zone.Id, PermissionFunctions.WaterMeasurements, PermissionAction.View, cancellationToken);
            // TODO(OMTS-219 W3): GFR-04 {last_date} is the later of the latest active measurement and the start of the
            // earliest Open cycle of the zone; until cycles exist, a zone never measured falls back to its creation date.
            var lastDate = BusinessClock.ToBusinessDate(latest.LatestMeasuredAt ?? zone.CreatedAt);
            await notifications.NotifyAsync(
                accountIds,
                new NotificationContent(
                    NotificationTypes.WaterMeasurementOverdue,
                    Title,
                    UserMessages.WaterMeasurementOverdue(zone.Name, lastDate),
                    new RelatedRecord(RelatedRecordTypes.FarmingZone, zone.Id, zone.Code)),
                cancellationToken);
            zone.WaterOverdueRemindedAt = now;
            reminded++;
        }

        if (reminded > 0)
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return reminded;
    }
}
