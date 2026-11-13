using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Entities.Cultivation;

namespace OMTS.Application.Features.Cultivation.Nursery;

/// <summary>
/// The notice of the nursery (decision of 12/11/2026): the Cooperative Owner is told when a batch is started, edited, cancelled, completed or corrected, unless the Owner did it. The notice is saved
/// with the change and pushed after it commits. The SRS has no message for it, so the text is a generic notice (<c>MINI_APP_NOTIFICATION</c>, as the lane notices) and opens the batch.
/// </summary>
public sealed class NurseryNotifier(IZoneRecipientQuery recipients, OwnerActorRecipientFilter ownerActorFilter, INotificationDispatcher notifications)
{
    public Task StartedAsync(NurseryBatch batch, Guid actor, CancellationToken cancellationToken) => NotifyAsync(
        "Nursery started", $"Nursery batch {batch.Code} started in zone {batch.Zone.Code}: {batch.QuantityIn:0.###} pieces.", batch, actor, cancellationToken);

    public Task EditedAsync(NurseryBatch batch, Guid actor, CancellationToken cancellationToken) => NotifyAsync(
        "Nursery edited", $"Nursery batch {batch.Code} in zone {batch.Zone.Code} was edited: {batch.QuantityIn:0.###} pieces.", batch, actor, cancellationToken);

    public Task CancelledAsync(NurseryBatch batch, string reason, Guid actor, CancellationToken cancellationToken) => NotifyAsync(
        "Nursery cancelled", $"Nursery batch {batch.Code} in zone {batch.Zone.Code} was cancelled: {reason}. The pieces are back in the stock.", batch, actor, cancellationToken);

    public Task CompletedAsync(NurseryBatch batch, Guid actor, CancellationToken cancellationToken) => NotifyAsync(
        "Nursery completed",
        $"Nursery batch {batch.Code} in zone {batch.Zone.Code} is done: about {batch.Survivors:0.###} of {batch.QuantityIn:0.###} pieces survived (counted by eye) and are ready to be seeded.",
        batch, actor, cancellationToken);

    public Task ClosedAsync(NurseryBatch batch, string? reason, Guid actor, CancellationToken cancellationToken) => NotifyAsync(
        "Nursery closed",
        $"Nursery batch {batch.Code} in zone {batch.Zone.Code} was closed: {batch.CloseLossPieces:0.###} pieces left were written off as an estimated loss{(string.IsNullOrWhiteSpace(reason) ? string.Empty : $" ({reason})")}.",
        batch, actor, cancellationToken);

    public Task SurvivorsCorrectedAsync(NurseryBatch batch, decimal from, string reason, Guid actor, CancellationToken cancellationToken) => NotifyAsync(
        "Nursery survivors corrected",
        $"Nursery batch {batch.Code} in zone {batch.Zone.Code}: survivors corrected from {from:0.###} to {batch.Survivors:0.###} pieces ({reason}).", batch, actor, cancellationToken);

    private async Task NotifyAsync(string title, string body, NurseryBatch batch, Guid actor, CancellationToken cancellationToken)
    {
        var told = await ownerActorFilter.WithoutOwnerActorAsync(await recipients.GetCooperativeOwnersAsync(cancellationToken), actor, cancellationToken);
        await notifications.NotifyAsync(told, NotificationTypes.NurseryRecorded, title, body, RelatedRecordTypes.NurseryBatch, batch.Code, isMandatory: false, cancellationToken, batch.Id);
    }
}
