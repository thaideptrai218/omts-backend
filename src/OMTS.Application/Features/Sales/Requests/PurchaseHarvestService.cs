using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Sales.PurchaseRequests;
using OMTS.Application.Features.Sales.Requests.Dtos;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Application.Features.Sales.Requests;

/// <summary>
/// The harvest mark of an Accepted request (decision of 11/11/2026): only the Cooperative Owner (API filter) marks it done once its lots
/// are in, which closes the request to new lots and opens it to the weighing, and clears it again while the request has no weighing.
/// </summary>
public sealed class PurchaseHarvestService(
    IPurchaseRequestRepository requests,
    IHarvestLotRepository lots,
    IPortWeighingRepository weighings,
    PurchaseRequestViews views,
    PurchaseHarvestReader harvest,
    ICurrentUser currentUser,
    IStatusCatalog statusCatalog,
    RowVersionGuard rowVersions,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    public async Task<Result<PurchaseRequestView>> MarkDoneAsync(Guid requestId, MarkHarvestDone entry, CancellationToken cancellationToken)
    {
        if (await requests.GetAsync(requestId, cancellationToken) is not { } request)
        {
            return AppError.NotFound;
        }

        if (request.StatusId != StatusIds.PurchaseRequest.Accepted)
        {
            return PurchaseRequestRules.WrongStatus(statusCatalog.DisplayNameOf(request.StatusId));
        }

        if (request.HarvestCompletedAt is not null)
        {
            return PurchaseRequestRules.WrongStatus(PurchaseRequestRules.HarvestDoneStatus);
        }

        if (await rowVersions.CheckAsync(request, entry.RowVersion, cancellationToken) is { } stale)
        {
            return stale;
        }

        if (!(await lots.CagesOfPurchaseRequestsAsync([request.Id], cancellationToken)).TryGetValue(request.Id, out var harvestedCages))
        {
            return PurchaseRequestRules.WrongStatus(PurchaseRequestRules.NoHarvestLotStatus);
        }

        request.HarvestCompletedAt = timeProvider.GetUtcNow();
        request.HarvestCompletedByAccountId = currentUser.AccountId;
        audit.Record(new AuditEntry("PurchaseRequest.HarvestDone", PurchaseRequestRules.RecordType)
        {
            RecordId = request.Id,
            RecordCode = request.Code,
            After = new { request.HarvestCompletedAt, HarvestedCages = harvestedCages, request.AcceptedCages },
        });

        // A lot recorded in parallel marks this row changed, so one of the two saves answers MSG06.
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await DetailAsync(request, cancellationToken);
    }

    public async Task<Result<PurchaseRequestView>> ClearDoneAsync(Guid requestId, MarkHarvestDone entry, CancellationToken cancellationToken)
    {
        if (await requests.GetAsync(requestId, cancellationToken) is not { } request)
        {
            return AppError.NotFound;
        }

        if (request.HarvestCompletedAt is not { } markedAt)
        {
            return PurchaseRequestRules.WrongStatus(PurchaseRequestRules.HarvestNotDoneStatus);
        }

        if (await rowVersions.CheckAsync(request, entry.RowVersion, cancellationToken) is { } stale)
        {
            return stale;
        }

        if (await weighings.AnyOfRequestAsync(request.Id, cancellationToken))
        {
            return PurchaseRequestRules.WrongStatus(PurchaseRequestRules.WeighedStatus);
        }

        request.HarvestCompletedAt = null;
        request.HarvestCompletedByAccountId = null;
        audit.Record(new AuditEntry("PurchaseRequest.HarvestDoneCleared", PurchaseRequestRules.RecordType)
        {
            RecordId = request.Id,
            RecordCode = request.Code,
            Before = new { HarvestCompletedAt = markedAt },
        });

        // A weighing saved in parallel marks this row changed, so one of the two saves answers MSG06.
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await DetailAsync(request, cancellationToken);
    }

    // Only the Owner gets here, so the commands are those of the Owner.
    private async Task<PurchaseRequestView> DetailAsync(PurchaseRequest request, CancellationToken cancellationToken) =>
        (await views.ForStaffAsync([request], cancellationToken))[0] with { Harvest = await harvest.ReadAsync(request, isOwner: true, cancellationToken) };
}
