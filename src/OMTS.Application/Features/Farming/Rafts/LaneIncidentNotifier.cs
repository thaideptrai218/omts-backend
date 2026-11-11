using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Application.Features.Farming.Rafts;

/// <summary>
/// The notices of lane incidents (decision of 12/11/2026): the Cooperative Owner is told when a lane is reported damaged or put out of service, unless the Owner did it. The notice is saved with the
/// incident and pushed after it commits. A repair is told to nobody: it only clears what the damage notice said. The SRS has no message for these, so the text is a generic notice
/// (<c>MINI_APP_NOTIFICATION</c>, as the other notices without an SRS code) and opens the raft.
/// </summary>
public sealed class LaneIncidentNotifier(IZoneRecipientQuery recipients, OwnerActorRecipientFilter ownerActorFilter, INotificationDispatcher notifications)
{
    public Task DamagedAsync(Raft raft, Lane lane, string reason, Guid actor, CancellationToken cancellationToken) => NotifyAsync(
        NotificationTypes.LaneDamaged,
        "Lane damaged",
        $"Raft {raft.Code}, lane {lane.LaneNumber} is reported damaged: {reason}. Seeding and harvest on it are blocked until it is repaired.",
        raft, actor, cancellationToken);

    public Task OutOfServiceAsync(Raft raft, Lane lane, string reason, bool hadSeed, Guid actor, CancellationToken cancellationToken) => NotifyAsync(
        NotificationTypes.LaneOutOfService,
        "Lane out of service",
        $"Raft {raft.Code}, lane {lane.LaneNumber} is out of service: {reason}.{(hadSeed ? " The seed on it is recorded as lost." : string.Empty)}",
        raft, actor, cancellationToken);

    private async Task NotifyAsync(string type, string title, string body, Raft raft, Guid actor, CancellationToken cancellationToken)
    {
        var told = await ownerActorFilter.WithoutOwnerActorAsync(await recipients.GetCooperativeOwnersAsync(cancellationToken), actor, cancellationToken);
        await notifications.NotifyAsync(told, type, title, body, RelatedRecordTypes.Raft, raft.Code, isMandatory: false, cancellationToken, raft.Id);
    }
}
