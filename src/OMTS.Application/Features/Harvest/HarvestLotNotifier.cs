using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Harvest;

namespace OMTS.Application.Features.Harvest;

/// <summary>
/// The notifications of a recorded harvest, saved with it and pushed after it commits. A harvest is operational, so an Owner is not told of their own action (PM decision OMTS-216);
/// a Zone Manager who finishes the last lane does get the reminder, as AC-13 says the Zone Managers of the zone receive it.
/// </summary>
public sealed class HarvestLotNotifier(IZoneRecipientQuery recipients, OwnerActorRecipientFilter ownerActorFilter, INotificationDispatcher notifications)
{
    private const string LotRecordedTitle = "New harvest lot";
    private const string CloseReminderTitle = "Reminder to close a cultivation cycle";

    /// <summary>MSG-HARVEST-01 to the Cooperative Owners (BF-03 step 7); not on a correction.</summary>
    public async Task LotRecordedAsync(HarvestLot lot, Raft raft, Guid recorder, CancellationToken cancellationToken)
    {
        var told = await ownerActorFilter.WithoutOwnerActorAsync(await recipients.GetCooperativeOwnersAsync(cancellationToken), recorder, cancellationToken);
        await notifications.NotifyAsync(
            told,
            new NotificationContent(
                NotificationTypes.HarvestLotRecorded,
                LotRecordedTitle,
                UserMessages.HarvestLotRecorded(raft.Code, lot.HarvestDate, lot.LotCode, lot.CageCount, lot.EstimatedKg),
                new RelatedRecord(RelatedRecordTypes.HarvestLot, lot.Id, lot.LotCode)),
            cancellationToken);
    }

    /// <summary>MSG-CYCLE-08 to the Zone Managers of the zone, or to the Cooperative Owners when it has none; always pushed (BR-CYCLE-07, BR-ACCOUNT-11).</summary>
    public async Task CloseReminderAsync(CultivationCycle cycle, Raft raft, Guid recorder, CancellationToken cancellationToken)
    {
        var told = await ownerActorFilter.WithoutOwnerActorAsync(await recipients.GetZoneManagersOrOwnersAsync(raft.ZoneId, cancellationToken), recorder, cancellationToken);
        await notifications.NotifyAsync(
            told,
            new NotificationContent(
                NotificationTypes.CycleCloseReminder,
                CloseReminderTitle,
                UserMessages.CycleCloseReminder(raft.Code),
                new RelatedRecord(RelatedRecordTypes.CultivationCycle, cycle.Id, raft.Code)),
            cancellationToken);
    }
}
