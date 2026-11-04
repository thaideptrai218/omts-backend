using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Notifications;

namespace OMTS.Application.Features.WaterQuality;

/// <summary>
/// GFR-04: the daily check of each zone In Use with an Open cycle (<see cref="WaterOverdueRule"/>). A zone that is due gets
/// MSG-WATER-06, kept and pushed (normal type), to its Zone Managers or, when it has none, to the Cooperative Owners. The
/// job has no actor, so nobody is left out as in the water alert. The zone holds the day of its last reminder.
/// </summary>
public sealed class WaterMeasurementOverdueReminder(
    IWaterMeasurementRepository measurements,
    ICultivationCycleQuery cycles,
    IZoneRecipientQuery recipients,
    INotificationDispatcher notifications,
    IUnitOfWork unitOfWork)
{
    public const string Title = "Water measurement overdue";

    /// <summary>Sends the reminders due on <paramref name="today"/> (business date); returns how many zones were reminded.</summary>
    public async Task<int> RunOnceAsync(DateOnly today, CancellationToken cancellationToken)
    {
        var openStarts = await cycles.EarliestOpenStartByZoneAsync(cancellationToken);
        if (openStarts.Count == 0)
        {
            return 0;
        }

        var latest = await measurements.LatestActiveByZoneAsync(cancellationToken);
        var zones = await measurements.ListZonesAsync(PermittedZones.Everywhere, inUseOnly: true, track: true, cancellationToken);
        var reminded = 0;
        foreach (var zone in zones)
        {
            if (!openStarts.TryGetValue(zone.Id, out var openStart))
            {
                continue;
            }

            var baseDate = WaterOverdueRule.BaseDate(latest.GetValueOrDefault(zone.Id)?.MeasuredAt, openStart);
            var lastSent = zone.WaterOverdueRemindedAt is { } sentAt ? BusinessClock.ToBusinessDate(sentAt) : (DateOnly?)null;
            if (!WaterOverdueRule.IsDue(baseDate, lastSent, today))
            {
                continue;
            }

            await notifications.NotifyAsync(
                await recipients.GetZoneManagersOrOwnersAsync(zone.Id, cancellationToken),
                new NotificationContent(
                    NotificationTypes.WaterMeasurementOverdue,
                    Title,
                    UserMessages.WaterMeasurementOverdue(zone.Name, baseDate),
                    new RelatedRecord(RelatedRecordTypes.FarmingZone, zone.Id, zone.Code)),
                cancellationToken);
            zone.WaterOverdueRemindedAt = BusinessClock.FromLocal(today.ToDateTime(TimeOnly.MinValue));
            reminded++;
        }

        if (reminded > 0)
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return reminded;
    }
}
