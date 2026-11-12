using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Harvest;
using OMTS.Application.Features.Purchasing;
using OMTS.Application.Features.Sales.Orders;
using OMTS.Domain.Constants;

namespace OMTS.Application.Features.Sales.Invoices;

/// <param name="Quantity">What the invoice proposes to deliver: the quantity still to deliver, 0 for a line already delivered in full (it stays so the seller can add to it).</param>
/// <param name="SuggestedLotId">The lot to take it from: a lot recorded from a harvest plan that serves the order first, then the other open lots of any zone the caller may invoice that hold the product; null when none does.</param>
public sealed record SalesInvoiceDraftLine(
    Guid SalesOrderLineId, int LineNumber, Guid ProductId, string ProductCode, string ProductName, Guid UnitId, string UnitCode, string UnitName, decimal Factor, decimal OrderedQuantity,
    decimal DeliveredQuantity, decimal Quantity, decimal UnitPrice, decimal ListPrice, decimal Amount, Guid? SuggestedLotId, string? SuggestedLotCode, bool SuggestedByPlan);

/// <summary>"Xử lý đặt hàng": the invoice an order proposes to make now. Nothing is saved.</summary>
/// <param name="Zone">The zone the invoice would take: the zone of the first lot proposed (an invoice takes the zone of its first lot); null when no lot is proposed.</param>
/// <param name="DiscountShare">The part of the order discount this invoice proposes to take, in proportion to its goods and never more than the earlier invoices left of it; the seller may change it.</param>
/// <param name="AdvanceLeft">What is left of the advance the customer paid on the order: it pays the invoice first.</param>
/// <param name="AdvanceUse">The part of the proposed total the advance would pay.</param>
public sealed record SalesInvoiceDraft(
    SalesInvoiceOrderState Order, SalesInvoiceZoneView? Zone, SalesInvoiceCustomerView? Customer, bool WalkInCustomer, IReadOnlyList<SalesInvoiceDraftLine> Lines, decimal Subtotal,
    decimal OrderDiscount, decimal DiscountShare, decimal Total, decimal AdvanceLeft, decimal AdvanceUse, string NextCode);

/// <summary>
/// GET sales-orders/{id}/invoice-draft: the lines still to deliver (ordered less delivered, never below 0) at the price of the order line and in its unit, the lot proposed for each (the lots of the harvest
/// plans that serve the order first), the advance left and the share of the order discount. The caller needs SALES_INVOICES Create in some zone; the lots proposed are those of the zones the caller may invoice in.
/// </summary>
public sealed class SalesInvoiceDraftService(
    ISalesOrderRepository orders,
    HarvestLotForSaleService forSale,
    SalesInvoiceCodeService codes,
    ZoneAccessGuard zoneAccess,
    IStatusCatalog statusCatalog)
{
    public async Task<Result<SalesInvoiceDraft>> DraftAsync(Guid orderId, CancellationToken cancellationToken)
    {
        var order = await orders.GetForEditAsync(orderId, cancellationToken);
        if (order is null)
        {
            return AppError.NotFound;
        }

        var permitted = await zoneAccess.PermittedZonesAsync(PermissionFunctions.SalesInvoices, PermissionAction.Create, cancellationToken);
        if (permitted.IsEmpty)
        {
            return AppError.Forbidden;
        }

        if (!SalesOrderRules.IsOpen(order.StatusId))
        {
            return AppError.Conflict(UserMessages.OrderNotOpenForInvoice(order.Code, statusCatalog.DisplayNameOf(order.StatusId)));
        }

        var delivered = await orders.DeliveredByLineAsync([order.Id], null, cancellationToken);
        var shared = (await orders.InvoicesOfAsync(order.Id, cancellationToken)).Where(i => i.StatusId != StatusIds.SalesInvoice.Cancelled).Sum(i => i.DiscountAmount);
        var advanceLeft = order.CustomerId is null ? 0m : (await orders.AdvancesOfAsync(order.Id, cancellationToken)).Sum(SalesOrderViews.Left);
        var lots = ((await forSale.ListAsync(null, order.Id, cancellationToken)).Value?.Lots ?? []).Where(l => permitted.Allows(l.ZoneId)).ToList();

        var lines = order.Lines.OrderBy(l => l.LineNumber).Select(l =>
        {
            var remaining = Math.Max(0m, l.Quantity - delivered.GetValueOrDefault(l.Id));
            // The lots are listed with the suggested ones first; a lot recorded before products holds none, so it is proposed for nothing.
            var lot = lots.FirstOrDefault(x => x.Products.Any(p => p.ProductId == l.ProductId));
            return new SalesInvoiceDraftLine(
                l.Id, l.LineNumber, l.ProductId, l.Product?.Code ?? string.Empty, l.Product?.Name ?? string.Empty, l.UnitId, l.UnitCode, l.UnitName, l.Factor, l.Quantity,
                delivered.GetValueOrDefault(l.Id), remaining, l.UnitPrice, l.ListPrice, InvoiceMoney.LineAmount(remaining, l.UnitPrice), lot?.Id, lot?.LotCode, lot?.Suggested ?? false);
        }).ToList();
        var subtotal = InvoiceMoney.Subtotal(lines.Select(l => l.Amount));
        var share = SalesInvoiceOrderShare.DiscountShare(order.DiscountAmount, order.Subtotal, subtotal, shared);
        var total = Math.Max(0m, subtotal - share);
        var first = lines.FirstOrDefault(l => l.SuggestedLotId is not null)?.SuggestedLotId is { } firstLotId ? lots.First(l => l.Id == firstLotId) : null;
        var state = new SalesInvoiceOrderState(order.Id, order.Code, order.StatusId, statusCatalog.DisplayNameOf(order.StatusId), CanFinish: false);
        return new SalesInvoiceDraft(
            state,
            first is null ? null : new SalesInvoiceZoneView(first.ZoneId, first.ZoneCode, first.ZoneName),
            order.Customer is { } c ? new SalesInvoiceCustomerView(c.Id, c.Name, c.PhoneNumber, c.Code) : null,
            order.CustomerId is null,
            lines, subtotal, order.DiscountAmount, share, total, advanceLeft, Math.Min(advanceLeft, total), await codes.PeekAsync(cancellationToken));
    }
}
