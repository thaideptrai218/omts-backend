using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Notifications;
using OMTS.Application.Features.Sales.PurchaseRequests;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Sales.QualityClaims;

/// <summary>
/// CLAIM-02 – only the Cooperative Owner responds (API filter), once, while the claim is Awaiting response (BR-CLAIM-02): a rejection, or an offer (a return, or a price cut with its own amount) that the
/// trader then accepts or declines (<see cref="QualityClaimAnswerService"/>, PM answer 12/11). The response changes neither the invoice nor the customer debt: a return is CLAIM-03 and a price cut
/// SALE-07, which the Owner runs once the trader has accepted; the offer here only records what was proposed. The response is saved first; the trader is told afterwards, and a message that is not
/// accepted undoes nothing (GFR-06).
/// </summary>
public sealed class QualityClaimResponseService(
    IQualityClaimRepository claims,
    IInvoiceSalesReturnCheck salesReturns,
    QualityClaimNotifications notifications,
    QualityClaimViews views,
    TraderMessageService traderMessages,
    ICurrentUser currentUser,
    IStatusCatalog statusCatalog,
    RowVersionGuard rowVersions,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    private const int MaxNoteLength = 1000;

    /// <summary>CLAIM-02.0: 409 MSG-CLAIM-03 when the invoice was cancelled or has a sales return; it can still be rejected.</summary>
    public async Task<Result<QualityClaimView>> AcceptAsync(Guid claimId, AcceptQualityClaim entry, CancellationToken cancellationToken)
    {
        var (claim, refused) = await LoadAwaitingAsync(claimId, entry.RowVersion, cancellationToken);
        if (refused is not null)
        {
            return refused;
        }

        var note = PurchaseRequestRules.CleanText(entry.Note);
        var invalid = new List<string>();
        if (entry.Offer is not { } offer || !Enum.IsDefined(offer))
        {
            invalid.Add("Offer");
        }

        if (note?.Length > MaxNoteLength)
        {
            invalid.Add("Note");
        }

        // The amount of a price cut is its own field: required for a price cut, above 0 and at most the invoice total, whole dong; none for a return.
        var amount = entry.PriceCutAmount;
        var needsAmount = entry.Offer == QualityClaimOffer.PriceCutWholeInvoice;
        if (needsAmount ? amount is not > 0m || amount > claim!.Invoice.Total || decimal.Round(amount.Value) != amount : amount is not null)
        {
            invalid.Add("Price cut amount");
        }

        if (invalid.Count > 0)
        {
            return AppError.InvalidFields(invalid);
        }

        var invoice = claim!.Invoice;
        var cause = invoice.StatusId == StatusIds.SalesInvoice.Cancelled ? "was cancelled"
            : await salesReturns.HasSalesReturnAsync(invoice.Id, cancellationToken) ? "has a sales return"
            : null;
        if (cause is not null)
        {
            return AppError.Conflict(UserMessages.QualityClaimCannotBeAccepted(invoice.Code, cause));
        }

        claim.Offer = entry.Offer;
        claim.OfferAmount = needsAmount ? amount : null;
        return await SaveResponseAsync(claim, StatusIds.QualityClaim.OfferMade, note, "QualityClaim.OfferMade", cancellationToken);
    }

    /// <summary>CLAIM-02.1: only the note is checked; a cancelled or returned invoice does not stop a rejection.</summary>
    public async Task<Result<QualityClaimView>> RejectAsync(Guid claimId, RejectQualityClaim entry, CancellationToken cancellationToken)
    {
        var (claim, refused) = await LoadAwaitingAsync(claimId, entry.RowVersion, cancellationToken);
        if (refused is not null)
        {
            return refused;
        }

        var note = PurchaseRequestRules.CleanText(entry.Note);
        if (note is null || note.Length > MaxNoteLength)
        {
            return AppError.Invalid("Note");
        }

        return await SaveResponseAsync(claim!, StatusIds.QualityClaim.Rejected, note, "QualityClaim.Rejected", cancellationToken);
    }

    // The status comes first: a claim already answered shows its status, not a stale form.
    private async Task<(QualityClaim? Claim, AppError? Refused)> LoadAwaitingAsync(Guid claimId, string? rowVersion, CancellationToken cancellationToken)
    {
        var claim = await claims.GetForEditAsync(claimId, cancellationToken);
        if (claim is null)
        {
            return (null, AppError.NotFound);
        }

        if (!QualityClaimRules.AllowsResponse(claim.StatusId))
        {
            return (null, QualityClaimRules.WrongStatus(statusCatalog.DisplayNameOf(claim.StatusId)));
        }

        return (claim, await rowVersions.CheckAsync(claim, rowVersion, cancellationToken));
    }

    private async Task<Result<QualityClaimView>> SaveResponseAsync(QualityClaim claim, int newStatusId, string? note, string auditAction, CancellationToken cancellationToken)
    {
        var before = claim.StatusId;
        claim.StatusId = newStatusId;
        claim.ResponseNote = note;
        claim.RespondedAt = timeProvider.GetUtcNow();
        claim.RespondedByAccountId = currentUser.AccountId;
        audit.Record(new AuditEntry(auditAction, QualityClaimRules.RecordType)
        {
            RecordId = claim.Id,
            RecordCode = claim.Code,
            ZoneId = claim.Invoice.ZoneId,
            Before = new { Status = statusCatalog.CodeOf(before) },
            After = new { Status = statusCatalog.CodeOf(newStatusId), claim.Offer, claim.OfferAmount, Note = note, claim.InvoiceId },
        });
        var sending = await notifications.ResponseAsync(claim, cancellationToken);
        if (await QualityClaimRules.SaveAsync(unitOfWork, claim, c => statusCatalog.DisplayNameOf(c.StatusId), cancellationToken) is { } lost)
        {
            return lost;
        }

        if (sending.DeliveryStatusId == StatusIds.MessageDelivery.Sending)
        {
            await traderMessages.DeliverAsync(sending);
        }

        var detail = await claims.GetDetailAsync(claim.Id, cancellationToken);
        var view = await views.ForStaffAsync(detail!, cancellationToken);
        var saved = UserMessages.Saved(QualityClaimRules.ObjectName);
        return view with { Message = saved.Text, MessageCode = saved.Code };
    }
}
