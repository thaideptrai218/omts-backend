using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Sales.Invoices;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Sales.Orders;

/// <summary>
/// A trader's purchase request sent from the Mini App becomes a Draft order (DH) at once, so the Owner does not enter it again. The order is for the customer of the request, from the source App, with one line: the
/// cages asked in the Lồng unit of the product (see <see cref="SalesOrderRequestLine"/>), priced at the sale price of that unit, to be collected on the pickup date. The request is then Ordered: the order carries on, and
/// the request no longer links lots or invoices. The order and the request are saved by the caller's save, so one never exists without the other. "Người nhận đặt" is the first active Cooperative Owner, who is also told of the request.
/// </summary>
public sealed class SalesOrderFromRequestService(
    ISalesOrderRepository orders,
    IProductRepository products,
    ICooperativeRepository cooperatives,
    ICooperativeOwnerQuery owners,
    SalesOrderCodeService codes,
    IStatusCatalog statusCatalog,
    AuditLogService audit)
{
    /// <summary>
    /// Adds the Draft order of <paramref name="request"/> to the unit of work and moves the request to Ordered; the caller saves. Null, with the request left as it is, when no order can be made: the product has no
    /// unit to count in, the line does not fit an amount, or the cooperative has no active Owner to receive it. The Owner then answers the request as before.
    /// </summary>
    public async Task<SalesOrder?> CreateDraftAsync(PurchaseRequest request, Guid traderAccountId, CancellationToken cancellationToken)
    {
        var receiver = (await owners.GetActiveOwnerIdsAsync(cancellationToken)).Cast<Guid?>().FirstOrDefault();
        var product = (await products.GetByIdsAsync([request.ProductId], cancellationToken)).FirstOrDefault();
        var cooperative = await cooperatives.GetCurrentAsync(cancellationToken);
        if (receiver is null || product is null || cooperative is null
            || SalesOrderRequestLine.Of(product, request.RequestedCages, cooperative.AverageKgPerCage) is not { } line
            || !SalesOrderRules.TryLineAmount(line.Quantity, line.UnitPrice, out var amount))
        {
            return null;
        }

        var unit = product.Units.First(u => u.UnitId == line.UnitId);
        var pickupAt = BusinessClock.FromLocal(request.PickupDate.ToDateTime(TimeOnly.MinValue));
        var order = new SalesOrder
        {
            OrderedAt = request.SentAt,
            OrderDate = BusinessClock.ToBusinessDate(request.SentAt),
            PickupAt = pickupAt,
            PickupDate = request.PickupDate,
            CustomerId = request.CustomerId,
            Customer = request.Customer,
            PurchaseRequestId = request.Id,
            PurchaseRequest = request,
            Source = SalesOrderSource.App,
            ReceivedByAccountId = receiver.Value,
            CreatedByAccountId = traderAccountId,
            Subtotal = amount,
            Total = amount,
            Note = request.Note,
            Code = await codes.NextAsync(cancellationToken),
        };
        order.Lines.Add(new SalesOrderLine
        {
            OrderId = order.Id,
            LineNumber = 1,
            ProductId = product.Id,
            UnitId = unit.UnitId,
            UnitCode = unit.Code,
            UnitName = unit.Unit.Name,
            Factor = unit.Factor,
            Quantity = line.Quantity,
            UnitPrice = line.UnitPrice,
            ListPrice = unit.SalePrice,
            Amount = amount,
        });
        orders.Add(order);
        foreach (var added in order.Lines)
        {
            orders.AddLine(added);
        }

        audit.Record(new AuditEntry("SalesOrder.Created", SalesOrderService.RecordType)
        {
            RecordId = order.Id,
            RecordCode = order.Code,
            ActorAccountId = traderAccountId,
            After = new
            {
                order.Code,
                Status = statusCatalog.CodeOf(order.StatusId),
                Source = order.Source.ToString(),
                order.CustomerId,
                order.PurchaseRequestId,
                order.PickupDate,
                order.Total,
                Lines = order.Lines.Select(l => new { l.ProductId, l.UnitCode, l.Quantity, l.UnitPrice, l.Amount }).ToList(),
            },
        });

        var from = request.StatusId;
        request.StatusId = StatusIds.PurchaseRequest.Ordered;
        audit.RecordStatusChange(nameof(PurchaseRequest), request.Id, request.Code, from, StatusIds.PurchaseRequest.Ordered);
        return order;
    }
}
