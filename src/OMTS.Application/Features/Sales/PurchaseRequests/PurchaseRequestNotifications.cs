using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Application.Features.Sales.PurchaseRequests;

/// <summary>
/// The three notification types of purchase requests (BR-SALE-03), added to the caller's save. The actor of a new or cancelled
/// request is always a trader and the response goes to the trader, so the Owner is never told of the Owner's own action here:
/// <see cref="OwnerActorRecipientFilter"/> has nothing to remove and is not used (BR-SALE-03 keeps the Owner in anyway).
/// </summary>
public sealed class PurchaseRequestNotifications(
    INotificationDispatcher notifications,
    ICooperativeOwnerQuery owners,
    ICooperativeRepository cooperatives,
    TraderMessageService traderMessages)
{
    // "kept short for SMS" (MSG-SALE-03): the whole note stays in the notification list and on the request.
    private const int MaxNoteInSms = 80;

    /// <summary>MSG-SALE-01, a mandatory type: always pushed (BR-ACCOUNT-11).</summary>
    public async Task NewRequestAsync(PurchaseRequest request, CancellationToken cancellationToken) => await notifications.NotifyAsync(
        await owners.GetActiveOwnerIdsAsync(cancellationToken),
        new NotificationContent(
            NotificationTypes.PurchaseRequestNew,
            "New purchase request",
            UserMessages.PurchaseRequestNew(request.Customer.Name, request.RequestedKg, request.Product.Name, request.PickupDate, request.Note),
            Record(request)),
        cancellationToken);

    /// <summary>MSG-SALE-02.</summary>
    public async Task CancelledAsync(PurchaseRequest request, CancellationToken cancellationToken) => await notifications.NotifyAsync(
        await owners.GetActiveOwnerIdsAsync(cancellationToken),
        new NotificationContent(
            NotificationTypes.PurchaseRequestCancelled,
            "Purchase request cancelled",
            UserMessages.PurchaseRequestCancelled(
                request.Customer.Name, BusinessClock.ToBusinessDate(request.SentAt), request.RequestedKg, request.Product.Name, request.CancelReason),
            Record(request)),
        cancellationToken);

    /// <summary>
    /// MSG-SALE-03 to the trader account, and the sending row when the profile has a phone number (BR-SALE-03, BR-SALE-09): the
    /// caller delivers it after the save. A request's profile always has an account: requests are made only from a linked
    /// profile, and a merge moves them only to a kept profile, never away from a linked one. The row needs that account, so a
    /// profile without one would get nothing; SALE-06 makes the recipient optional when it sends to profiles with no account.
    /// </summary>
    public async Task<Notification?> ResponseAsync(PurchaseRequest request, CancellationToken cancellationToken)
    {
        if (request.Customer.AccountId is not { } accountId)
        {
            return null;
        }

        var cooperative = await cooperatives.GetCurrentAsync(cancellationToken)
            ?? throw new InvalidOperationException("The cooperative has not been created yet (run the bootstrap command).");
        // {cooperative_short_name}: empty until the Owner first saves the profile (FARM-01), when the full name stands in.
        var cooperativeName = cooperative.ShortName ?? cooperative.FullName;
        await notifications.NotifyAsync([accountId], Content(request, cooperativeName, request.ResponseNote), cancellationToken);
        return request.Customer.PhoneNumber is { } phone
            ? traderMessages.Queue(accountId, phone, Content(request, cooperativeName, ForSms(request.ResponseNote)))
            : null;
    }

    /// <summary>
    /// MSG-ORDER-04 to the trader account and, when the profile has a phone number, the sending row: the request became the order (DH). Sent through the channel of the response to a request
    /// (<see cref="NotificationTypes.PurchaseRequestResponse"/>); the caller delivers the row after its save, as for a response.
    /// </summary>
    public async Task<Notification?> OrderedAsync(PurchaseRequest request, SalesOrder order, CancellationToken cancellationToken)
    {
        if (request.Customer.AccountId is not { } accountId)
        {
            return null;
        }

        var cooperative = await cooperatives.GetCurrentAsync(cancellationToken)
            ?? throw new InvalidOperationException("The cooperative has not been created yet (run the bootstrap command).");
        var content = new NotificationContent(
            NotificationTypes.PurchaseRequestResponse,
            "Your purchase request was ordered",
            UserMessages.OrderPlacedNotice(cooperative.ShortName ?? cooperative.FullName, order.Code, order.PickupAt),
            Record(request));
        await notifications.NotifyAsync([accountId], content, cancellationToken);
        return request.Customer.PhoneNumber is { } phone ? traderMessages.Queue(accountId, phone, content) : null;
    }

    private static NotificationContent Content(PurchaseRequest request, string cooperativeName, string? note) => new(
        NotificationTypes.PurchaseRequestResponse,
        "Response to your purchase request",
        request.AcceptedKg is { } acceptedKg
            ? UserMessages.PurchaseRequestAccepted(
                cooperativeName, request.RequestedKg, request.Product.Name, acceptedKg, request.AgreedPickupDate!.Value, request.PayLaterAgreed == true, note)
            : UserMessages.PurchaseRequestDeclined(cooperativeName, request.RequestedKg, request.Product.Name, note!),
        Record(request));

    private static RelatedRecord Record(PurchaseRequest request) => new(RelatedRecordTypes.PurchaseRequest, request.Id);

    private static string? ForSms(string? note) => TraderMessageText.Shorten(note, MaxNoteInSms);
}
