using OMTS.Application.Common;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Application.Features.Sales.PurchaseRequests;

/// <summary>
/// SALE-03 – only the Cooperative Owner responds (API filter), once, while the request is Awaiting response (BR-SALE-02). The
/// response is saved first; the trader is told afterwards, and a message that is not accepted undoes nothing (GFR-06).
/// </summary>
public sealed class PurchaseRequestResponseService(
    IPurchaseRequestRepository requests,
    PurchaseRequestNotifications notifications,
    PurchaseRequestViews views,
    TraderMessageService traderMessages,
    ICurrentUser currentUser,
    IStatusCatalog statusCatalog,
    RowVersionGuard rowVersions,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    /// <summary>SALE-03.0: the accepted kg is the requested kg or fewer, and any date not before today, also an unchanged one.</summary>
    public async Task<Result<PurchaseRequestView>> AcceptAsync(Guid requestId, AcceptPurchaseRequest entry, CancellationToken cancellationToken)
    {
        var (request, refused) = await LoadAwaitingAsync(requestId, entry.RowVersion, cancellationToken);
        if (refused is not null)
        {
            return refused;
        }

        var note = PurchaseRequestRules.CleanText(entry.Note);
        var invalid = new List<string>();
        if (!PurchaseRequestRules.IsKg(entry.AcceptedKg) || entry.AcceptedKg > request!.RequestedKg)
        {
            invalid.Add("Accepted kg");
        }

        if (entry.PickupDate is not { } pickupDate || pickupDate < timeProvider.Today())
        {
            invalid.Add("Pickup date");
        }

        if (entry.PayLaterAgreed is null)
        {
            invalid.Add("Pay later");
        }

        if (note?.Length > PurchaseRequestRules.MaxTextLength)
        {
            invalid.Add("Note");
        }

        if (invalid.Count > 0)
        {
            return AppError.InvalidFields(invalid);
        }

        request!.AcceptedKg = entry.AcceptedKg;
        request.AgreedPickupDate = entry.PickupDate;
        request.PayLaterAgreed = entry.PayLaterAgreed;
        return await SaveResponseAsync(request, StatusIds.PurchaseRequest.Accepted, note, "PurchaseRequest.Accepted", cancellationToken);
    }

    /// <summary>SALE-03.1: only the note is checked.</summary>
    public async Task<Result<PurchaseRequestView>> DeclineAsync(Guid requestId, DeclinePurchaseRequest entry, CancellationToken cancellationToken)
    {
        var (request, refused) = await LoadAwaitingAsync(requestId, entry.RowVersion, cancellationToken);
        if (refused is not null)
        {
            return refused;
        }

        var note = PurchaseRequestRules.CleanText(entry.Note);
        if (note is null || note.Length > PurchaseRequestRules.MaxTextLength)
        {
            return AppError.Invalid("Note");
        }

        return await SaveResponseAsync(request!, StatusIds.PurchaseRequest.Declined, note, "PurchaseRequest.Declined", cancellationToken);
    }

    // SALE-03.0.E1 comes first: a request the trader cancelled answers its status, not a stale form.
    private async Task<(PurchaseRequest? Request, AppError? Refused)> LoadAwaitingAsync(Guid requestId, string? rowVersion, CancellationToken cancellationToken)
    {
        var request = await requests.GetForEditAsync(requestId, cancellationToken);
        if (request is null)
        {
            return (null, AppError.NotFound);
        }

        if (!PurchaseRequestRules.AllowsResponse(request.StatusId))
        {
            return (null, PurchaseRequestRules.WrongStatus(statusCatalog.DisplayNameOf(request.StatusId)));
        }

        return (request, await rowVersions.CheckAsync(request, rowVersion, cancellationToken));
    }

    private async Task<Result<PurchaseRequestView>> SaveResponseAsync(PurchaseRequest request, int newStatusId, string? note, string auditAction, CancellationToken cancellationToken)
    {
        var before = request.StatusId;
        request.StatusId = newStatusId;
        request.ResponseNote = note;
        request.RespondedAt = timeProvider.GetUtcNow();
        request.RespondedByAccountId = currentUser.AccountId;
        audit.Record(new AuditEntry(auditAction, PurchaseRequestRules.RecordType)
        {
            RecordId = request.Id,
            Before = new { Status = statusCatalog.CodeOf(before) },
            After = new { Status = statusCatalog.CodeOf(newStatusId), request.AcceptedKg, request.AgreedPickupDate, request.PayLaterAgreed, Note = note },
        });
        var sending = await notifications.ResponseAsync(request, cancellationToken);
        if (await PurchaseRequestRules.SaveAsync(unitOfWork, request, PurchaseRequestRules.AllowsResponse, statusCatalog.DisplayNameOf, cancellationToken) is { } lost)
        {
            return lost;
        }

        if (sending is not null)
        {
            await traderMessages.DeliverAsync(sending);
        }

        return (await views.ForStaffAsync([request], cancellationToken))[0];
    }
}
