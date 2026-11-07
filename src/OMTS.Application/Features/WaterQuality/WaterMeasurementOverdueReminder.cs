using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Application.Features.WaterQuality;

/// <summary>
/// GFR-04: the daily check of each zone In Use with an Open cycle (<see cref="WaterOverdueRule"/>), one zone per call so
/// that a conflict on one zone costs only that zone. A zone that is due gets MSG-WATER-06, kept and pushed (normal type), to
/// its Zone Managers or, when it has none, to the Cooperative Owners. The job has no actor, so nobody is left out as in the
/// water alert. The zone holds the day of its last reminder, saved with the notifications: a lost save keeps neither, and
/// its push is never sent, so the next try sends one.
/// </summary>
public sealed class WaterMeasurementOverdueReminder(
    IWaterMeasurementRepository measurements,
    ICultivationCycleQuery cycles,
    IZoneRecipientQuery recipients,
    INotificationDispatcher notifications,
    IUnitOfWork unitOfWork)
{
    public const string Title = "Water measurement overdue";

    /// <summary>Zones with a reminder due on <paramref name="today"/> (business date); <see cref="RemindAsync"/> re-checks each one.</summary>
    public async Task<IReadOnlyList<Guid>> CandidateZoneIdsAsync(DateOnly today, CancellationToken cancellationToken)
    {
        var openStarts = await cycles.EarliestOpenStartByZoneAsync(cancellationToken);
        if (openStarts.Count == 0)
        {
            return [];
        }

        var latest = await measurements.LatestActiveByZoneAsync(cancellationToken);
        var zones = await measurements.ListZonesAsync(PermittedZones.Everywhere, inUseOnly: true, track: false, cancellationToken);
        return zones
            .Where(zone => openStarts.TryGetValue(zone.Id, out var openStart)
                && IsDue(zone, WaterOverdueRule.BaseDate(latest.GetValueOrDefault(zone.Id)?.MeasuredAt, openStart), today))
            .Select(zone => zone.Id)
            .ToList();
    }

    /// <summary>
    /// Sends the reminder of the zone if it is still due, reading the zone, its cycles and its measurements again; true when
    /// someone was notified. With nobody to notify the marker stays as it is, so the next day checks the zone again.
    /// </summary>
    /// <exception cref="Common.Exceptions.ConcurrencyConflictException">The zone was saved after it was read here; nothing is kept.</exception>
    public async Task<bool> RemindAsync(Guid zoneId, DateOnly today, CancellationToken cancellationToken)
    {
        var zone = await measurements.GetZoneAsync(zoneId, track: true, cancellationToken);
        var openStarts = await cycles.EarliestOpenStartByZoneAsync(cancellationToken);
        if (zone?.StatusId != StatusIds.Usage.InUse || !openStarts.TryGetValue(zoneId, out var openStart))
        {
            return false;
        }

        var latest = await measurements.ListLatestActiveAsync(zoneId, 1, cancellationToken);
        var baseDate = WaterOverdueRule.BaseDate(latest.FirstOrDefault()?.MeasuredAt, openStart);
        if (!IsDue(zone, baseDate, today))
        {
            return false;
        }

        var accounts = await recipients.GetZoneManagersOrOwnersAsync(zoneId, cancellationToken);
        if (accounts.Count == 0)
        {
            return false;
        }

        await notifications.NotifyAsync(
            accounts,
            new NotificationContent(
                NotificationTypes.WaterMeasurementOverdue,
                Title,
                UserMessages.WaterMeasurementOverdue(zone.Name, baseDate),
                new RelatedRecord(RelatedRecordTypes.FarmingZone, zone.Id, zone.Code)),
            cancellationToken);
        zone.WaterOverdueRemindedAt = BusinessClock.FromLocal(today.ToDateTime(TimeOnly.MinValue));
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static bool IsDue(FarmingZone zone, DateOnly baseDate, DateOnly today)
    {
        var lastSent = zone.WaterOverdueRemindedAt is { } sentAt ? BusinessClock.ToBusinessDate(sentAt) : (DateOnly?)null;
        return WaterOverdueRule.IsDue(baseDate, lastSent, today);
    }
}
