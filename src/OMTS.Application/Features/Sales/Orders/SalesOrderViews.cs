using OMTS.Application.Common;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Sales.Payments;
using OMTS.Application.Features.WaterQuality.Dtos;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Sales.Orders;

/// <summary>Builds the answers: one order with its lines, advances and buttons, and the rows of the list.</summary>
public sealed class SalesOrderViews(
    IAccountRepository accounts,
    IStatusCatalog statusCatalog,
    ISalesOrderRepository orders,
    IHarvestPlanRepository harvestPlans,
    SalesOrderAccess access,
    SalesOrderProgress progress,
    TimeProvider timeProvider)
{
    /// <summary>An advance counts as money of the customer once it is Completed or Awaiting confirmation; one waiting for its transfer or cancelled counts nowhere.</summary>
    public static bool Counts(CustomerPayment payment) =>
        payment.Kind == CustomerPaymentKind.Payment && payment.StatusId is StatusIds.CustomerPayment.Completed or StatusIds.CustomerPayment.AwaitingConfirmation;

    /// <summary>What is left of an advance: its amount less the parts set against invoices; 0 when it does not count.</summary>
    public static decimal Left(CustomerPayment payment) => Counts(payment) ? Math.Max(0m, payment.Amount + payment.DiscountAmount - payment.Allocations.Sum(a => a.Amount)) : 0m;

    public async Task<SalesOrderView> ViewAsync(SalesOrder order, CancellationToken cancellationToken)
    {
        var advances = await orders.AdvancesOfAsync(order.Id, cancellationToken);
        var delivered = await progress.DeliveredByLineAsync([order.Id], cancellationToken);
        var orderInvoices = await orders.InvoicesOfAsync(order.Id, cancellationToken);
        var hasLiveInvoice = orderInvoices.Any(i => i.StatusId != StatusIds.SalesInvoice.Cancelled);
        var invoiced = orderInvoices.Where(i => i.StatusId != StatusIds.SalesInvoice.Cancelled).Sum(i => i.Total);
        var caller = await access.CallerAsync(cancellationToken);
        var servedBy = (await harvestPlans.PlansOfOrdersAsync([order.Id], cancellationToken))
            .Where(p => caller.IsOwner || p.WasAssigned)
            .Select(p => new SalesOrderHarvestPlanView(p.PlanId, p.PlanCode, p.StatusId, statusCatalog.DisplayNameOf(p.StatusId), p.PlannedAt)).ToList();
        var ids = new[] { order.ReceivedByAccountId, order.CreatedByAccountId }.Concat(advances.Select(a => a.CollectedByAccountId).OfType<Guid>())
            .Concat(orderInvoices.Select(i => i.CreatedByAccountId)).Distinct().ToList();
        var names = await accounts.GetDisplayNamesAsync(ids, cancellationToken);
        AccountRef? Ref(Guid? id) => id is { } value ? new AccountRef(value, names.GetValueOrDefault(value) ?? string.Empty) : null;

        var lines = LineViews(order, delivered);
        var holds = SalesOrderRules.HoldsAdvance(order.StatusId);
        var advanceViews = advances.Select(a => new SalesOrderAdvanceView(
            a.Id, a.Code, a.Amount, a.Method, a.StatusId, statusCatalog.DisplayNameOf(a.StatusId), a.PaidAt, a.BankReference, Ref(a.CollectedByAccountId),
            a.Allocations.Sum(p => p.Amount), holds && Counts(a))).ToList();
        var paid = advances.Where(Counts).Sum(a => a.Amount);
        return new SalesOrderView(
            order.Id, order.Code, order.StatusId, statusCatalog.DisplayNameOf(order.StatusId), order.OrderedAt, order.PickupAt,
            order.Customer is { } c ? new SalesOrderCustomerView(c.Id, c.Code, c.Name, c.PhoneNumber) : null, order.CustomerId is null,
            order.Source, Ref(order.ReceivedByAccountId) ?? new AccountRef(order.ReceivedByAccountId, string.Empty), Ref(order.CreatedByAccountId),
            order.Note, order.Subtotal, order.DiscountAmount, order.DiscountDecidedByOwner, order.Total, invoiced, paid,
            advances.Sum(Left), holds,
            lines, Progress(lines),
            advanceViews,
            orderInvoices.Select(i => new SalesOrderInvoiceView(i.Id, i.Code, i.InvoicedAt, i.Total, i.StatusId, statusCatalog.DisplayNameOf(i.StatusId), Ref(i.CreatedByAccountId))).ToList(),
            servedBy,
            order.PurchaseRequest is { } r ? new SalesOrderRequestView(r.Id, r.SentAt, r.StatusId, statusCatalog.DisplayNameOf(r.StatusId)) : null,
            SalesOrderRules.AllowedActions(order.StatusId, caller, hasLiveInvoice, order.CustomerId is not null),
            order.CreatedAt, order.CompletedAt, order.CompletedManually, order.CancelledAt, order.CancelReason, RowVersionGuard.Encode(order.RowVersion));
    }

    public async Task<IReadOnlyList<SalesOrderListItem>> ListItemsAsync(IReadOnlyList<SalesOrderRow> rows, CancellationToken cancellationToken)
    {
        var ids = rows.SelectMany(r => new[] { r.Order.ReceivedByAccountId, r.Order.CreatedByAccountId }).Distinct().ToList();
        var names = ids.Count == 0 ? new Dictionary<Guid, string>() : await accounts.GetDisplayNamesAsync(ids, cancellationToken);
        var delivered = await progress.DeliveredByLineAsync(rows.Select(r => r.Order.Id).ToList(), cancellationToken);
        var today = timeProvider.Today();
        return rows.Select(r =>
        {
            var o = r.Order;
            return new SalesOrderListItem(
                o.Id, o.Code, o.StatusId, statusCatalog.DisplayNameOf(o.StatusId), o.OrderedAt, o.PickupAt, SalesOrderRules.WaitingDays(o.StatusId, o.PickupDate, today),
                o.CustomerId, o.Customer?.Code, o.Customer?.Name, o.Customer?.PhoneNumber, o.CustomerId is null, o.Source,
                names.GetValueOrDefault(o.ReceivedByAccountId), names.GetValueOrDefault(o.CreatedByAccountId),
                o.Total, r.InvoicedTotal, r.CustomerPaid, r.InvoiceCodes ?? [], Progress(LineViews(o, delivered)), RowVersionGuard.Encode(o.RowVersion));
        }).ToList();
    }

    private static List<SalesOrderLineView> LineViews(SalesOrder order, IReadOnlyDictionary<Guid, decimal> delivered) =>
        order.Lines.OrderBy(l => l.LineNumber).Select(l => new SalesOrderLineView(
            l.Id, l.LineNumber, l.ProductId, l.Product?.Code ?? string.Empty, l.Product?.Name ?? string.Empty, l.UnitId, l.UnitCode, l.UnitName, l.Factor, l.Quantity,
            l.UnitPrice, l.ListPrice, l.Amount, delivered.GetValueOrDefault(l.Id), l.Note)).ToList();

    /// <summary>"x/y" per unit: what is delivered of what was ordered, the lines of one unit added up.</summary>
    private static IReadOnlyList<SalesOrderProgressView> Progress(IEnumerable<SalesOrderLineView> lines) =>
        lines.GroupBy(l => l.UnitId).Select(g => new SalesOrderProgressView(g.Key, g.First().UnitCode, g.First().UnitName, g.Sum(l => l.Quantity), g.Sum(l => l.DeliveredQuantity))).ToList();
}
