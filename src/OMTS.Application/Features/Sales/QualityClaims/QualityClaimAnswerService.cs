using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Sales.PurchaseRequests;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Application.Features.Sales.QualityClaims;

/// <summary>
/// CLAIM-02, the trader's side (PM answer 12/11: the Owner proposes, the trader accepts or declines): the trader whose profile the claim names answers the offer of the Owner once, while the claim is
/// Offer made. A claim of another trader is not found. The answer changes neither the invoice nor the customer debt: the return (CLAIM-03) or the price cut (SALE-07) that an accepted offer leads to
/// is run by the Owner. It is audited with the trader as the actor.
/// </summary>
public sealed class QualityClaimAnswerService(
    IQualityClaimRepository claims,
    QualityClaimViews views,
    IStatusCatalog statusCatalog,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    private const int MaxNoteLength = 1000;

    public Task<Result<QualityClaimSaved>> AcceptAsync(Guid accountId, Guid claimId, AnswerQualityClaimOffer entry, CancellationToken cancellationToken) =>
        AnswerAsync(accountId, claimId, entry, StatusIds.QualityClaim.Accepted, "QualityClaim.OfferAccepted", cancellationToken);

    public Task<Result<QualityClaimSaved>> DeclineAsync(Guid accountId, Guid claimId, AnswerQualityClaimOffer entry, CancellationToken cancellationToken) =>
        AnswerAsync(accountId, claimId, entry, StatusIds.QualityClaim.OfferDeclined, "QualityClaim.OfferDeclined", cancellationToken);

    private async Task<Result<QualityClaimSaved>> AnswerAsync(
        Guid accountId, Guid claimId, AnswerQualityClaimOffer? entry, int newStatusId, string auditAction, CancellationToken cancellationToken)
    {
        var claim = await claims.GetForEditAsync(claimId, cancellationToken);
        if (claim is null || claim.Customer.AccountId != accountId)
        {
            return AppError.NotFound;
        }

        // The status first: a claim already answered shows its status, not a stale form.
        if (!QualityClaimRules.AllowsAnswer(claim.StatusId))
        {
            return QualityClaimRules.WrongStatus(statusCatalog.DisplayNameOf(claim.StatusId));
        }

        var note = PurchaseRequestRules.CleanText(entry?.Note);
        if (note?.Length > MaxNoteLength)
        {
            return AppError.Invalid("Note");
        }

        var before = claim.StatusId;
        claim.StatusId = newStatusId;
        claim.AnsweredAt = timeProvider.GetUtcNow();
        claim.AnswerNote = note;
        audit.Record(new AuditEntry(auditAction, QualityClaimRules.RecordType)
        {
            RecordId = claim.Id,
            RecordCode = claim.Code,
            ZoneId = claim.Invoice.ZoneId,
            Before = new { Status = statusCatalog.CodeOf(before) },
            After = new { Status = statusCatalog.CodeOf(newStatusId), claim.Offer, claim.OfferAmount, Note = note, claim.InvoiceId },
        });
        if (await QualityClaimRules.SaveAsync(unitOfWork, claim, c => statusCatalog.DisplayNameOf(c.StatusId), QualityClaimRules.AllowsAnswer, cancellationToken) is { } lost)
        {
            return lost;
        }

        var detail = await claims.GetDetailAsync(claim.Id, cancellationToken);
        var saved = UserMessages.Saved(QualityClaimRules.ObjectName);
        return new QualityClaimSaved(await views.ForTraderAsync(detail!, cancellationToken), saved.Text, saved.Code);
    }
}
