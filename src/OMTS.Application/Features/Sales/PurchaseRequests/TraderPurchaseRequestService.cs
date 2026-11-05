using OMTS.Application.Common;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Application.Features.Sales.PurchaseRequests;

/// <summary>
/// SALE-01 and SALE-02 – what a trader does, always on the customer profile linked to the account (BR-ACCOUNT-04). A request of
/// another trader answers as not found. Requests of a profile merged into this one were re-pointed by the merge (SALE-04.3), so
/// they count as its own.
/// </summary>
public sealed class TraderPurchaseRequestService(
    IPurchaseRequestRepository requests,
    ICustomerRepository customers,
    IProductRepository products,
    PurchaseRequestNotifications notifications,
    PurchaseRequestViews views,
    IPurchaseRequestFulfilment fulfilment,
    IStatusCatalog statusCatalog,
    RowVersionGuard rowVersions,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    /// <summary>
    /// SALE-01. The profile row is written too (<see cref="ICustomerReferences"/>): a merge at this moment conflicts, and so
    /// does a second tap on Send. A product discontinued in the very moment of the save is not caught: the request names it,
    /// reserves nothing, and the Owner declines it.
    /// </summary>
    public async Task<Result<PurchaseRequestSaved>> PlaceAsync(Guid accountId, PlacePurchaseRequest entry, CancellationToken cancellationToken)
    {
        if (await customers.GetByAccountIdAsync(accountId, cancellationToken) is not { } customer)
        {
            return AppError.Forbidden;
        }

        var product = entry.ProductId is { } productId ? await products.GetForEditAsync(productId, cancellationToken) : null;
        var note = PurchaseRequestRules.CleanText(entry.Note);
        var invalid = new List<string>();
        if (product is null)
        {
            invalid.Add("Product");
        }

        if (!PurchaseRequestRules.IsKg(entry.RequestedKg))
        {
            invalid.Add("Requested kg");
        }

        if (entry.PickupDate is not { } pickupDate || pickupDate < timeProvider.Today())
        {
            invalid.Add("Pickup date");
        }

        if (note?.Length > PurchaseRequestRules.MaxTextLength)
        {
            invalid.Add("Note");
        }

        if (invalid.Count > 0)
        {
            return AppError.InvalidFields(invalid);
        }

        if (product!.StatusId != StatusIds.Usage.InUse)
        {
            return AppError.NotUsableChoice($"Product {product.Name}", statusCatalog.DisplayNameOf(product.StatusId), "Product");
        }

        var request = new PurchaseRequest
        {
            CustomerId = customer.Id,
            Customer = customer,
            ProductId = product.Id,
            Product = product,
            RequestedKg = entry.RequestedKg!.Value,
            PickupDate = entry.PickupDate!.Value,
            Note = note,
            SentAt = timeProvider.GetUtcNow(),
        };
        requests.Add(request);
        unitOfWork.MarkChanged(customer);
        audit.Record(new AuditEntry("PurchaseRequest.Sent", PurchaseRequestRules.RecordType)
        {
            RecordId = request.Id,
            ActorAccountId = accountId,
            After = new { Status = statusCatalog.CodeOf(request.StatusId), request.CustomerId, request.ProductId, ProductName = product.Name, request.RequestedKg, request.PickupDate, request.Note },
        });
        await notifications.NewRequestAsync(request, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        var saved = UserMessages.Saved(PurchaseRequestRules.ObjectName);
        return new PurchaseRequestSaved((await views.ForTraderAsync([request], cancellationToken))[0], saved.Text, saved.Code);
    }

    /// <summary>
    /// SALE-02. The status comes first: a request that can no longer be cancelled answers its status whatever the form holds.
    /// A form read before the Owner answered gets MSG06, so the trader sees the response before cancelling.
    /// </summary>
    public async Task<Result<TraderPurchaseRequestView>> CancelAsync(Guid accountId, Guid requestId, CancelPurchaseRequest entry, CancellationToken cancellationToken)
    {
        var request = await requests.GetForEditAsync(requestId, cancellationToken);
        if (request is null || request.Customer.AccountId != accountId)
        {
            return AppError.NotFound;
        }

        if (!PurchaseRequestRules.AllowsCancel(request.StatusId))
        {
            return PurchaseRequestRules.WrongStatus(statusCatalog.DisplayNameOf(request.StatusId));
        }

        if ((await fulfilment.FulfilledAmongAsync([request.Id], cancellationToken)).Contains(request.Id))
        {
            return PurchaseRequestRules.WrongStatus(PurchaseRequestRules.FulfilledStatus);
        }

        if (await rowVersions.CheckAsync(request, entry.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        var reason = PurchaseRequestRules.CleanText(entry.Reason);
        if (reason?.Length > PurchaseRequestRules.MaxTextLength)
        {
            return AppError.Invalid("Reason");
        }

        var before = request.StatusId;
        request.StatusId = StatusIds.PurchaseRequest.Cancelled;
        request.CancelReason = reason;
        request.CancelledAt = timeProvider.GetUtcNow();
        audit.Record(new AuditEntry("PurchaseRequest.Cancelled", PurchaseRequestRules.RecordType)
        {
            RecordId = request.Id,
            ActorAccountId = accountId,
            Before = new { Status = statusCatalog.CodeOf(before) },
            After = new { Status = statusCatalog.CodeOf(request.StatusId) },
            Reason = reason,
        });
        await notifications.CancelledAsync(request, cancellationToken);
        if (await PurchaseRequestRules.SaveAsync(unitOfWork, request, PurchaseRequestRules.AllowsCancel, r => Task.FromResult(statusCatalog.DisplayNameOf(r.StatusId)), cancellationToken) is { } lost)
        {
            return lost;
        }

        return (await views.ForTraderAsync([request], cancellationToken))[0];
    }
}
