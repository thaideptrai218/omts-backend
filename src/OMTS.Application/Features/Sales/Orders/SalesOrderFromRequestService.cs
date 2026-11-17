using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Sales.Invoices;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Sales.Orders;

/// <summary>
/// A trader's purchase request sent from the Mini App also makes a Draft order (DH) at once, so the Owner does not enter it again. The order is for the customer of the request, from the source App, with one line: the
/// cages asked in the Lồng unit of the product (see <see cref="SalesOrderRequestLine"/>), priced at the sale price of that unit, to be collected on the pickup date. The request is not answered by it: it stays Awaiting
/// response until the Owner accepts or declines it, and the trader cancelling it cancels the Draft order too (<see cref="CancelDraftAsync"/>). The order and the request are saved by the caller's save, so one never exists
/// without the other. "Người nhận đặt" is the first active Cooperative Owner, who is also told of the request.
/// </summary>
public sealed class SalesOrderFromRequestService(
    ISalesOrderRepository orders,
    IProductRepository products,
    ICooperativeRepository cooperatives,
    ICooperativeOwnerQuery owners,
    SalesOrderCodeService codes,
    IStatusCatalog statusCatalog,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    /// <summary>
    /// MSG-GEN-01 on the field Product when the line the request would make has no price: the unit it is sold in (the Lồng unit, else kg) is on the price list at 0. The request is refused before anything is saved, so the
    /// order is never made at 0 and the Owner never has to notice it. Null when the line is priced, or when no line can be made at all (<see cref="CreateDraftAsync"/> then leaves the Owner to answer the request).
    /// </summary>
    public async Task<AppError?> PriceRefusalAsync(Guid productId, decimal cages, CancellationToken cancellationToken)
    {
        var product = (await products.GetByIdsAsync([productId], cancellationToken)).FirstOrDefault();
        var cooperative = await cooperatives.GetCurrentAsync(cancellationToken);
        return product is not null && cooperative is not null && SalesOrderRequestLine.Of(product, cages, cooperative.AverageKgPerCage) is { UnitPrice: <= 0m }
            ? AppError.Invalid(ProductField, "The product has no price for the unit it is sold in.")
            : null;
    }

    /// <summary>The field a request names its product by.</summary>
    public const string ProductField = "Product";

    /// <summary>
    /// Adds the Draft order of <paramref name="request"/> to the unit of work, leaving the request as it is (Awaiting response); the caller saves. Null when no order can be made: the product has no
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

        return order;
    }

    /// <summary>
    /// The trader cancelled <paramref name="request"/>: the order the request made is cancelled with it, while it is still a Draft (no invoice was made from it). An order the Owner already took further, or one that
    /// is no longer there, is left as it is. The order is marked changed, so an invoice saved in the same moment and this save cannot both win; the caller saves. True when an order was cancelled.
    /// </summary>
    public async Task<bool> CancelDraftAsync(PurchaseRequest request, Guid traderAccountId, string? reason, CancellationToken cancellationToken)
    {
        if (await orders.FindByPurchaseRequestAsync(request.Id, cancellationToken) is not { StatusId: StatusIds.SalesOrder.Draft } found
            || await orders.GetForEditAsync(found.Id, cancellationToken) is not { StatusId: StatusIds.SalesOrder.Draft } order)
        {
            return false;
        }

        order.StatusId = StatusIds.SalesOrder.Cancelled;
        order.CancelledAt = timeProvider.GetUtcNow();
        order.CancelledByAccountId = traderAccountId;
        order.CancelReason = reason ?? CancelledWithRequest;
        unitOfWork.MarkChanged(order);
        audit.Record(new AuditEntry("SalesOrder.Cancelled", SalesOrderService.RecordType)
        {
            RecordId = order.Id,
            RecordCode = order.Code,
            ActorAccountId = traderAccountId,
            Before = new { Status = statusCatalog.CodeOf(StatusIds.SalesOrder.Draft) },
            After = new { Status = statusCatalog.CodeOf(order.StatusId) },
            Reason = order.CancelReason,
        });
        return true;
    }

    /// <summary>The reason kept on the order when the trader gave none for cancelling the request.</summary>
    public const string CancelledWithRequest = "The trader cancelled the purchase request.";
}
