using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Infrastructure.Persistence.Queries;

/// <summary>
/// The end-of-day reads: the orders, invoices (not Cancelled) and returns that stand of a period given in hours, the money brought in with them, the lines of the invoices and the lots weighed.
/// Read only, no tracking; a fixed number of commands per method whatever the number of rows.
/// </summary>
internal sealed class EndOfDayReportQuery(OmtsDbContext db) : IEndOfDayReportQuery
{
    public async Task<EndOfDaySalesFacts> SalesAsync(EndOfDayScope scope, EndOfDaySalesCriteria criteria, CancellationToken cancellationToken)
    {
        var (from, to, fromDate, toDate) = Bounds(scope);
        var zones = scope.ZoneIds?.ToList();
        var customers = criteria.CustomerIds.ToList();
        var receivedBy = criteria.ReceivedBy.ToList();
        var createdBy = criteria.CreatedBy.ToList();
        var methods = criteria.PaymentMethods.ToList();
        var channels = criteria.Channels.ToList();
        var terms = criteria.PaymentTerms.ToList();

        // Money that came in: a payment that is not cancelled and not waiting for its transfer.
        var counted = db.CustomerPayments.AsNoTracking()
            .Where(p => p.Kind == CustomerPaymentKind.Payment && p.StatusId != StatusIds.CustomerPayment.Cancelled && p.StatusId != StatusIds.CustomerPayment.PendingTransfer);
        var inPeriod = counted.Where(p => p.PaidAt >= from && p.PaidAt < to);

        var invoices = db.SalesInvoices.AsNoTracking()
            .Where(i => i.InvoiceDate >= fromDate && i.InvoiceDate <= toDate && i.InvoicedAt >= from && i.InvoicedAt < to && i.StatusId != StatusIds.SalesInvoice.Cancelled);
        var returns = db.SalesReturns.AsNoTracking().Where(r => r.CreatedAt >= from && r.CreatedAt < to && r.StatusId == StatusIds.SalesReturn.Recorded);
        var orders = db.SalesOrders.AsNoTracking()
            .Where(o => o.OrderDate >= fromDate && o.OrderDate <= toDate && o.OrderedAt >= from && o.OrderedAt < to && o.StatusId != StatusIds.SalesOrder.Cancelled);

        if (zones is not null)
        {
            invoices = invoices.Where(i => zones.Contains(i.ZoneId));
            returns = returns.Where(r => zones.Contains(r.Invoice.ZoneId));
            orders = orders.Where(o => (o.ZoneId != null && zones.Contains(o.ZoneId.Value)) || db.SalesInvoices.Any(i => i.SalesOrderId == o.Id && zones.Contains(i.ZoneId)));
        }

        if (customers.Count > 0)
        {
            invoices = invoices.Where(i => i.CustomerId != null && customers.Contains(i.CustomerId.Value));
            returns = returns.Where(r => r.Invoice.CustomerId != null && customers.Contains(r.Invoice.CustomerId.Value));
            orders = orders.Where(o => o.CustomerId != null && customers.Contains(o.CustomerId.Value));
        }

        if (receivedBy.Count > 0)
        {
            invoices = invoices.Where(i => i.SalesOrder != null && receivedBy.Contains(i.SalesOrder.ReceivedByAccountId));
            returns = returns.Where(r => r.Invoice.SalesOrder != null && receivedBy.Contains(r.Invoice.SalesOrder.ReceivedByAccountId));
            orders = orders.Where(o => receivedBy.Contains(o.ReceivedByAccountId));
        }

        if (createdBy.Count > 0)
        {
            invoices = invoices.Where(i => createdBy.Contains(i.CreatedByAccountId));
            returns = returns.Where(r => createdBy.Contains(r.RecordedByAccountId));
            orders = orders.Where(o => createdBy.Contains(o.CreatedByAccountId));
        }

        if (channels.Count > 0)
        {
            invoices = invoices.Where(i => i.SalesOrder != null && channels.Contains(i.SalesOrder.Source));
            returns = returns.Where(r => r.Invoice.SalesOrder != null && channels.Contains(r.Invoice.SalesOrder.Source));
            orders = orders.Where(o => channels.Contains(o.Source));
        }

        if (terms.Count > 0)
        {
            invoices = invoices.Where(i => terms.Contains(i.PaymentTerm));
            returns = returns.Where(r => terms.Contains(r.Invoice.PaymentTerm));
        }

        if (methods.Count > 0)
        {
            invoices = invoices.Where(i => inPeriod.Any(p => p.InvoiceId == i.Id && methods.Contains(p.Method)));
            orders = orders.Where(o => inPeriod.Any(p => p.SalesOrderId == o.Id && p.InvoiceId == null && methods.Contains(p.Method)));
        }

        var orderRows = terms.Count > 0 ? [] : await ReadOrdersAsync(orders, inPeriod, cancellationToken);
        var invoiceRows = await ReadInvoicesAsync(invoices, counted, db.CustomerPaymentAllocations.AsNoTracking(), from, to, cancellationToken);
        var returnRows = methods.Count > 0
            ? []
            : await returns
                .OrderBy(r => r.CreatedAt).ThenBy(r => r.Code)
                .Select(r => new EndOfDayReturnFact(
                    r.Id, r.Code, r.CreatedAt, r.InvoiceId, r.Invoice.Code, r.Invoice.CustomerId, r.Invoice.Customer == null ? null : r.Invoice.Customer.Name, r.Invoice.Zone.Name,
                    r.Lines.Sum(l => l.ReturnedKg), r.Value, r.Invoice.OtherCharges))
                .ToListAsync(cancellationToken);
        return new EndOfDaySalesFacts(orderRows, invoiceRows, returnRows);
    }

    private static async Task<List<EndOfDayOrderFact>> ReadOrdersAsync(IQueryable<SalesOrder> orders, IQueryable<CustomerPayment> inPeriod, CancellationToken cancellationToken)
    {
        // A deposit names the order and no invoice; a payment that names an invoice is counted with that invoice.
        var deposits = await inPeriod
            .Where(p => p.SalesOrderId != null && p.InvoiceId == null && orders.Any(o => o.Id == p.SalesOrderId))
            .GroupBy(p => p.SalesOrderId!.Value)
            .Select(g => new { OrderId = g.Key, Amount = g.Sum(p => p.Amount) })
            .ToDictionaryAsync(x => x.OrderId, x => x.Amount, cancellationToken);
        return (await orders
                .OrderBy(o => o.OrderedAt).ThenBy(o => o.Code)
                .Select(o => new
                {
                    o.Id,
                    o.Code,
                    o.OrderedAt,
                    o.CustomerId,
                    CustomerName = o.Customer == null ? null : o.Customer.Name,
                    ZoneName = o.Zone == null ? null : o.Zone.Name,
                    Kg = o.Lines.Sum(l => l.Quantity * l.Factor),
                    o.Subtotal,
                    o.DiscountAmount,
                })
                .ToListAsync(cancellationToken))
            .Select(o => new EndOfDayOrderFact(o.Id, o.Code, o.OrderedAt, o.CustomerId, o.CustomerName, o.ZoneName, o.Kg, o.Subtotal, o.DiscountAmount, deposits.GetValueOrDefault(o.Id)))
            .ToList();
    }

    private static async Task<List<EndOfDayInvoiceFact>> ReadInvoicesAsync(
        IQueryable<SalesInvoice> invoices, IQueryable<CustomerPayment> counted, IQueryable<CustomerPaymentAllocation> allocations, DateTimeOffset from, DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        // Money set against an invoice through an allocation (the advance of its order, a later debt payment) settles it too, so the debt subtracts it.
        // An allocation of a payment that already names the same invoice is skipped, so nothing is subtracted twice.
        var allocated = await allocations
            .Where(a => invoices.Any(i => i.Id == a.InvoiceId) && counted.Any(p => p.Id == a.PaymentId && (p.InvoiceId == null || p.InvoiceId != a.InvoiceId)))
            .GroupBy(a => a.InvoiceId)
            .Select(g => new { InvoiceId = g.Key, Amount = g.Sum(a => a.Amount) })
            .ToDictionaryAsync(x => x.InvoiceId, x => x.Amount, cancellationToken);
        var paid = await counted
            .Where(p => p.InvoiceId != null && invoices.Any(i => i.Id == p.InvoiceId))
            .GroupBy(p => p.InvoiceId!.Value)
            .Select(g => new { InvoiceId = g.Key, Total = g.Sum(p => p.Amount), InPeriod = g.Sum(p => p.PaidAt >= from && p.PaidAt < to ? p.Amount : 0m) })
            .ToDictionaryAsync(x => x.InvoiceId, x => (x.Total, x.InPeriod), cancellationToken);
        return (await invoices
                .OrderBy(i => i.InvoicedAt).ThenBy(i => i.Code)
                .Select(i => new
                {
                    i.Id,
                    i.Code,
                    i.InvoicedAt,
                    i.CustomerId,
                    CustomerName = i.Customer == null ? null : i.Customer.Name,
                    ZoneName = i.Zone.Name,
                    Kg = i.Lines.Sum(l => l.Kg),
                    i.Subtotal,
                    Discount = i.DiscountAmount + i.PriceCutTotal,
                    i.OtherCharges,
                    i.Total,
                    ReturnedValue = i.Returns.Where(r => r.StatusId == StatusIds.SalesReturn.Recorded).Sum(r => r.Value),
                })
                .ToListAsync(cancellationToken))
            .Select(i =>
            {
                var (direct, inPeriod) = paid.GetValueOrDefault(i.Id);
                var total = direct + allocated.GetValueOrDefault(i.Id);
                return new EndOfDayInvoiceFact(
                    i.Id, i.Code, i.InvoicedAt, i.CustomerId, i.CustomerName, i.ZoneName, i.Kg, i.Subtotal, i.Discount, i.OtherCharges, i.Total, inPeriod, total, i.ReturnedValue);
            })
            .ToList();
    }

    public async Task<EndOfDayGoodsFacts> GoodsAsync(EndOfDayScope scope, EndOfDayGoodsCriteria criteria, CancellationToken cancellationToken)
    {
        var (from, to, fromDate, toDate) = Bounds(scope);
        var zones = scope.ZoneIds?.ToList();
        var customers = criteria.CustomerIds.ToList();
        var createdBy = criteria.CreatedBy.ToList();
        var invoices = db.SalesInvoices.AsNoTracking()
            .Where(i => i.InvoiceDate >= fromDate && i.InvoiceDate <= toDate && i.InvoicedAt >= from && i.InvoicedAt < to && i.StatusId != StatusIds.SalesInvoice.Cancelled);
        var returns = db.SalesReturns.AsNoTracking().Where(r => r.CreatedAt >= from && r.CreatedAt < to && r.StatusId == StatusIds.SalesReturn.Recorded);
        if (zones is not null)
        {
            invoices = invoices.Where(i => zones.Contains(i.ZoneId));
            returns = returns.Where(r => zones.Contains(r.Invoice.ZoneId));
        }

        if (customers.Count > 0)
        {
            invoices = invoices.Where(i => i.CustomerId != null && customers.Contains(i.CustomerId.Value));
            returns = returns.Where(r => r.Invoice.CustomerId != null && customers.Contains(r.Invoice.CustomerId.Value));
        }

        if (createdBy.Count > 0)
        {
            invoices = invoices.Where(i => createdBy.Contains(i.CreatedByAccountId));
            returns = returns.Where(r => createdBy.Contains(r.RecordedByAccountId));
        }

        var lines = await invoices.SelectMany(i => i.Lines)
            .Select(l => new EndOfDayLineFact(
                l.Id, l.InvoiceId, l.Invoice.Code, l.Invoice.InvoicedAt, l.Invoice.Customer == null ? null : l.Invoice.Customer.Name, l.LineNumber, l.ProductId, l.Product.Code,
                l.Product.Name, l.Product.CategoryId, l.Kg, l.Amount, l.Invoice.DiscountAmount + l.Invoice.PriceCutTotal))
            .ToListAsync(cancellationToken);
        var returned = await returns.SelectMany(r => r.Lines.Select(rl => new EndOfDayReturnedLineFact(
                r.Id, r.Code, r.CreatedAt, r.InvoiceId, r.Invoice.Code, r.Invoice.Customer == null ? null : r.Invoice.Customer.Name, rl.InvoiceLineId, rl.ReturnedKg)))
            .ToListAsync(cancellationToken);

        // The invoice of a return may be older than the period: its lines are needed to share its discount.
        var loaded = lines.Select(l => l.InvoiceId).ToHashSet();
        var missing = returned.Select(r => r.InvoiceId).Where(id => !loaded.Contains(id)).Distinct().ToList();
        var returnedInvoiceLines = missing.Count == 0
            ? []
            : await db.SalesInvoices.AsNoTracking().Where(i => missing.Contains(i.Id)).SelectMany(i => i.Lines)
                .Select(l => new EndOfDayLineFact(
                    l.Id, l.InvoiceId, l.Invoice.Code, l.Invoice.InvoicedAt, l.Invoice.Customer == null ? null : l.Invoice.Customer.Name, l.LineNumber, l.ProductId, l.Product.Code,
                    l.Product.Name, l.Product.CategoryId, l.Kg, l.Amount, l.Invoice.DiscountAmount + l.Invoice.PriceCutTotal))
                .ToListAsync(cancellationToken);
        return new EndOfDayGoodsFacts(lines, returned, returnedInvoiceLines);
    }

    public async Task<IReadOnlyList<EndOfDayLot>> LotsAsync(EndOfDayScope scope, CancellationToken cancellationToken)
    {
        var (from, to, fromDate, toDate) = Bounds(scope);
        var lots = db.HarvestLots.AsNoTracking()
            .Where(l => l.WeighedKg != null && l.WeighedOn >= fromDate && l.WeighedOn <= toDate && l.WeighedAt >= from && l.WeighedAt < to);
        if (scope.ZoneIds?.ToList() is { } zones)
        {
            lots = lots.Where(l => zones.Contains(l.Raft.ZoneId));
        }

        return (await lots
                .OrderBy(l => l.WeighedAt)
                .Select(l => new { l.Id, l.LotCode, l.Raft.ZoneId, ZoneName = l.Raft.Zone.Name, Kg = l.WeighedKg!.Value, At = l.WeighedAt })
                .ToListAsync(cancellationToken))
            .Select(l => new EndOfDayLot(l.Id, l.LotCode, l.ZoneId, l.ZoneName, l.Kg, l.At ?? default))
            .ToList();
    }

    public async Task<IReadOnlyList<EndOfDayCategory>> CategoriesAsync(CancellationToken cancellationToken) =>
        await db.ProductCategories.AsNoTracking().Select(c => new EndOfDayCategory(c.Id, c.Name, c.ParentId)).ToListAsync(cancellationToken);

    /// <summary>The instants of the scope and the business dates they fall on, so the date columns narrow the read before the exact instants do.</summary>
    private static (DateTimeOffset From, DateTimeOffset To, DateOnly FromDate, DateOnly ToDate) Bounds(EndOfDayScope scope) =>
        (scope.From, scope.ToExclusive, BusinessClock.ToBusinessDate(scope.From), BusinessClock.ToBusinessDate(scope.ToExclusive.AddTicks(-1)));
}
