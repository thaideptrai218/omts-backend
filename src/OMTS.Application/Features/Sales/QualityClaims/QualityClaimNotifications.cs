using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Features.Notifications;
using OMTS.Application.Features.Sales.Invoices;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Application.Features.Sales.QualityClaims;

/// <summary>
/// MSG-CLAIM-01 and MSG-CLAIM-02, added to the caller's save (GFR-06): the push goes after it commits, and the SMS row is delivered by the caller afterwards, so a message that fails undoes nothing.
/// </summary>
public sealed class QualityClaimNotifications(
    INotificationDispatcher notifications,
    ICooperativeOwnerQuery owners,
    ICooperativeRepository cooperatives,
    TraderMessageService traderMessages)
{
    // "kept short for SMS" as for a purchase request: the whole note stays in the notification list and on the claim.
    private const int MaxNoteInSms = 80;
    private const int MaxDescriptionStart = 100;

    /// <summary>MSG-CLAIM-01 to the Cooperative Owners; not a mandatory type (BR-CLAIM-01).</summary>
    public async Task NewClaimAsync(QualityClaim claim, string invoiceCode, string customerName, CancellationToken cancellationToken) => await notifications.NotifyAsync(
        await owners.GetActiveOwnerIdsAsync(cancellationToken),
        new NotificationContent(
            NotificationTypes.QualityClaimNew,
            "New quality claim",
            UserMessages.QualityClaimNew(customerName, invoiceCode, TraderMessageText.Shorten(claim.Description, MaxDescriptionStart)!),
            Record(claim)),
        cancellationToken);

    /// <summary>
    /// MSG-CLAIM-02 to the trader account and the phone number of the profile (BR-CLAIM-02). The sending row is Not sent, with no phone, when the profile has none: staff then see they must call. The
    /// caller delivers a row that is Sending after the save.
    /// </summary>
    public async Task<Notification> ResponseAsync(QualityClaim claim, CancellationToken cancellationToken)
    {
        var cooperative = await cooperatives.GetCurrentAsync(cancellationToken)
            ?? throw new InvalidOperationException("The cooperative has not been created yet (run the bootstrap command).");
        var cooperativeName = cooperative.ShortName ?? cooperative.FullName;
        var accountId = claim.Customer.AccountId;
        var toList = Content(claim, cooperativeName, claim.ResponseNote, cooperative.PhoneNumber);
        if (accountId is { } account)
        {
            await notifications.NotifyAsync([account], toList, cancellationToken);
        }

        var forSms = Content(claim, cooperativeName, TraderMessageText.Shorten(claim.ResponseNote, MaxNoteInSms), cooperative.PhoneNumber);
        return claim.Customer.PhoneNumber is { } phone
            ? traderMessages.Queue(accountId, phone, forSms)
            : traderMessages.NotSent(accountId, forSms, SalesInvoiceMessages.NoPhoneNumber);
    }

    private static NotificationContent Content(QualityClaim claim, string cooperativeName, string? note, string? cooperativePhone) => new(
        NotificationTypes.QualityClaimResponse,
        "Response to your quality claim",
        UserMessages.QualityClaimResponse(
            cooperativeName, claim.Invoice.Code, claim.StatusId != StatusIds.QualityClaim.Rejected, claim.Offer?.ToString(), note, cooperativePhone, claim.OfferAmount),
        Record(claim));

    private static RelatedRecord Record(QualityClaim claim) => new(RelatedRecordTypes.QualityClaim, claim.Id, claim.Code);
}
