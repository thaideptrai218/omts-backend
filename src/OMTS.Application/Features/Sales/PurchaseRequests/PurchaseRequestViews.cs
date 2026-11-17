using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Sales.Debts;
using OMTS.Application.Features.Sales.Requests;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Application.Features.Sales.PurchaseRequests;

/// <summary>
/// Builds the answers: Fulfilled is worked out once per list (BR-SALE-01), and only staff get the profile, the sendings and the
/// staffing plan.
/// </summary>
public sealed class PurchaseRequestViews(
    IPurchaseRequestRepository requests,
    IPurchaseRequestFulfilment fulfilment,
    ISalesOrderRepository orders,
    CustomerDebtReader debts,
    IStatusCatalog statusCatalog)
{
    public async Task<IReadOnlyList<PurchaseRequestView>> ForStaffAsync(IReadOnlyList<PurchaseRequest> rows, CancellationToken cancellationToken)
    {
        var ids = rows.Select(r => r.Id).ToList();
        var fulfilled = await FulfilledAsync(rows, cancellationToken);
        var sendings = (await requests.ListSendingsAsync(ids, cancellationToken)).ToLookup(s => s.RequestId);
        var owed = await debts.DebtsAsync(rows.Select(r => r.CustomerId).ToList(), cancellationToken);
        var ordersOf = await OrdersOfAsync(rows, cancellationToken);
        return rows.Select(r =>
        {
            var isFulfilled = fulfilled.Contains(r.Id);
            return new PurchaseRequestView(
                r.Id,
                new PurchaseRequestCustomerView(r.CustomerId, r.Customer.Name, r.Customer.PhoneNumber, owed[r.CustomerId], r.Customer.Code),
                r.ProductId, r.Product.Name, r.RequestedCages, r.PickupDate, r.Note, r.SentAt,
                r.StatusId, StatusName(r, isFulfilled), isFulfilled, Response(r), r.CancelReason, r.CancelledAt,
                sendings[r.Id].Select(Sending).ToList(),
                RowVersionGuard.Encode(r.RowVersion), ordersOf.GetValueOrDefault(r.Id)?.Id, ordersOf.GetValueOrDefault(r.Id)?.Code,
                r.Code, PurchaseStaffingService.Summary(r), r.HarvestCompletedAt);
        }).ToList();
    }

    public async Task<IReadOnlyList<TraderPurchaseRequestView>> ForTraderAsync(IReadOnlyList<PurchaseRequest> rows, CancellationToken cancellationToken)
    {
        var fulfilled = await FulfilledAsync(rows, cancellationToken);
        var ordersOf = await OrdersOfAsync(rows, cancellationToken);
        return rows.Select(r =>
        {
            var isFulfilled = fulfilled.Contains(r.Id);
            return new TraderPurchaseRequestView(
                r.Id, r.ProductId, r.Product.Name, r.RequestedCages, r.PickupDate, r.Note, r.SentAt,
                r.StatusId, StatusName(r, isFulfilled), isFulfilled, PurchaseRequestRules.AllowsCancel(r.StatusId) && !isFulfilled,
                Response(r), r.CancelReason, r.CancelledAt,
                RowVersionGuard.Encode(r.RowVersion),
                r.Code, r.HarvestCompletedAt, ordersOf.GetValueOrDefault(r.Id)?.Code);
        }).ToList();
    }

    /// <summary>The order of each request that has one (a request has at most one): the Draft order a Mini App request made, or the order the Owner made from it; one query for the whole list.</summary>
    private async Task<IReadOnlyDictionary<Guid, SalesOrder>> OrdersOfAsync(IReadOnlyList<PurchaseRequest> rows, CancellationToken cancellationToken)
    {
        var ordered = rows.Where(r => r.StatusId is StatusIds.PurchaseRequest.Ordered or StatusIds.PurchaseRequest.AwaitingResponse or StatusIds.PurchaseRequest.AwaitingStaff or StatusIds.PurchaseRequest.Accepted).Select(r => r.Id).ToList();
        return ordered.Count == 0
            ? new Dictionary<Guid, SalesOrder>()
            : (await orders.FindByPurchaseRequestsAsync(ordered, cancellationToken)).ToDictionary(o => o.PurchaseRequestId!.Value);
    }

    private async Task<IReadOnlySet<Guid>> FulfilledAsync(IReadOnlyList<PurchaseRequest> rows, CancellationToken cancellationToken) =>
        await fulfilment.FulfilledAmongAsync(rows.Where(r => r.StatusId == StatusIds.PurchaseRequest.Accepted).Select(r => r.Id).ToList(), cancellationToken);

    private string StatusName(PurchaseRequest r, bool fulfilled) => fulfilled ? PurchaseRequestRules.FulfilledStatus : statusCatalog.DisplayNameOf(r.StatusId);

    private static PurchaseRequestResponseView? Response(PurchaseRequest r) =>
        r.RespondedAt is { } at ? new PurchaseRequestResponseView(r.AcceptedCages, r.AgreedPickupDate, r.PayLaterAgreed, r.ResponseNote, at) : null;

    private PurchaseRequestSendingView Sending(PurchaseRequestSending s) => new(
        s.Channel.ToString(), s.PhoneNumber, s.DeliveryStatusId, s.DeliveryStatusId is { } id ? statusCatalog.DisplayNameOf(id) : null, s.DeliveryStatusAt);
}
