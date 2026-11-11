using OMTS.Application.Common;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Messaging;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Notifications;
using OMTS.Application.Features.Sales.Requests;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Application.Features.Sales.PurchaseRequests;

/// <summary>
/// SALE-03 – only the Cooperative Owner responds (API filter), once, while the request is Awaiting response or Awaiting staff
/// (BR-SALE-02). Accepting needs the staffing plan agreed by enough staff (decision of 31/10/2026). The response is saved first; the
/// trader is told afterwards, and a message that is not accepted undoes nothing (GFR-06).
/// </summary>
public sealed class PurchaseRequestResponseService(
    IPurchaseRequestRepository requests,
    PurchaseRequestNotifications notifications,
    PurchaseRequestViews views,
    IPurchaseRequestFulfilment fulfilment,
    PurchaseStaffingService staffing,
    INotificationDispatcher dispatcher,
    TraderMessageService traderMessages,
    ICurrentUser currentUser,
    IStatusCatalog statusCatalog,
    RowVersionGuard rowVersions,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    /// <summary>
    /// SALE-03.0: the accepted cages are the requested cages or fewer, and any date not before today, also an unchanged one. They are the
    /// cages and the pickup date of the staffing plan; the shifts the staff agreed to become their work assignments.
    /// </summary>
    public async Task<Result<PurchaseRequestView>> AcceptAsync(Guid requestId, AcceptPurchaseRequest entry, CancellationToken cancellationToken)
    {
        var (request, refused) = await LoadAwaitingAsync(requestId, entry.RowVersion, cancellationToken);
        if (refused is not null)
        {
            return refused;
        }

        var note = PurchaseRequestRules.CleanText(entry.Note);
        var invalid = new List<string>();
        if (!PurchaseRequestRules.IsCages(entry.AcceptedCages) || entry.AcceptedCages > request!.RequestedCages)
        {
            invalid.Add("Accepted cages");
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

        if (await staffing.ValidateAcceptanceAsync(request!, entry.AcceptedCages, entry.PickupDate, cancellationToken) is { } notStaffed)
        {
            return notStaffed;
        }

        request!.AcceptedCages = entry.AcceptedCages;
        request.AgreedPickupDate = entry.PickupDate;
        request.PayLaterAgreed = entry.PayLaterAgreed;
        await staffing.ActivateAssignmentsAsync(request, cancellationToken);
        var staff = request.ShiftInvitations.Select(i => i.Employee.AccountId).OfType<Guid>().Distinct().ToList();
        await dispatcher.NotifyAsync(staff, "PURCHASE_SHIFT_CONFIRMED", "HTX đã nhận đơn",
            $"{request.Code}: HTX đã nhận đơn. Xem trạng thái ca ngày {request.WorkDate:dd/MM/yyyy} trong Công việc của tôi.",
            nameof(PurchaseRequest), request.Code, false, cancellationToken, request.Id);
        return await SaveResponseAsync(request, StatusIds.PurchaseRequest.Accepted, note, "PurchaseRequest.Accepted", cancellationToken);
    }

    /// <summary>SALE-03.1: only the note is checked. The shifts the staff agreed to are let go.</summary>
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

        await staffing.ReleaseAsync(request!, cancellationToken);
        return await SaveResponseAsync(request!, StatusIds.PurchaseRequest.Declined, note, "PurchaseRequest.Declined", cancellationToken);
    }

    // SALE-03.0.E1 comes first: a request the trader cancelled answers its status, not a stale form.
    private async Task<(PurchaseRequest? Request, AppError? Refused)> LoadAwaitingAsync(Guid requestId, string? rowVersion, CancellationToken cancellationToken)
    {
        var request = await requests.GetAsync(requestId, cancellationToken);
        if (request is null)
        {
            return (null, AppError.NotFound);
        }

        if (!PurchaseRequestRules.AllowsResponse(request.StatusId))
        {
            return (null, PurchaseRequestRules.WrongStatus(await StatusNameAsync(request, cancellationToken)));
        }

        return (request, await rowVersions.CheckAsync(request, rowVersion, cancellationToken));
    }

    // SALE-03.0.E1 "shows its status", as the screen reads it: Fulfilled is worked out by the hook and has no status id.
    private async Task<string> StatusNameAsync(PurchaseRequest request, CancellationToken cancellationToken) =>
        request.StatusId == StatusIds.PurchaseRequest.Accepted && (await fulfilment.FulfilledAmongAsync([request.Id], cancellationToken)).Contains(request.Id)
            ? PurchaseRequestRules.FulfilledStatus
            : statusCatalog.DisplayNameOf(request.StatusId);

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
            RecordCode = request.Code,
            Before = new { Status = statusCatalog.CodeOf(before) },
            After = new { Status = statusCatalog.CodeOf(newStatusId), request.AcceptedCages, request.AgreedPickupDate, request.PayLaterAgreed, Note = note },
        });
        var sending = await notifications.ResponseAsync(request, cancellationToken);
        if (await PurchaseRequestRules.SaveAsync(unitOfWork, request, PurchaseRequestRules.AllowsResponse, r => StatusNameAsync(r, cancellationToken), cancellationToken) is { } lost)
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
