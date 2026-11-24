using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Reports;
using OMTS.Application.Features.Reports.SalesPerformance;
using OMTS.Application.Features.Sales.Debts;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Infrastructure.Persistence.Queries;

/// <summary>
/// REPORT-01 (BR-REPORT-01) on the indexes of the invoices (InvoiceDate, ZoneId). An invoice is in the period by its invoice date (the business date, UTC+7); a Cancelled one never counts;
/// the revenue is the total of the invoice at its latest values, the same three conditions as the daily revenue of FARM-06. A sales return counts when it stands (not cancelled), whatever its
/// date. Read only; a fixed number of commands whatever the number of invoices or customers.
/// </summary>
internal sealed class SalesPerformanceQuery(OmtsDbContext db) : ISalesPerformanceQuery
{
    public async Task<SalesPerformanceAggregates> AggregatesAsync(ReportFilter filter, SalesPerformanceGrouping grouping, CancellationToken cancellationToken)
    {
        var invoices = InvoicesOf(filter);
        switch (grouping)
        {
            case SalesPerformanceGrouping.Customer:
            {
                // The walk-in invoices have no customer: they are the slice with no id.
                var sums = await SumsByAsync(invoices, i => i.CustomerId ?? Guid.Empty, cancellationToken);
                var wanted = Wanted(sums.Keys);
                var names = await NamesAsync(db.Customers.AsNoTracking().Where(c => wanted.Contains(c.Id)).Select(c => new Named(c.Id, c.Name)), cancellationToken);
                return Sliced(sums.Select(s => new SalesPerformanceSlice(s.Key == Guid.Empty ? null : s.Key, names.GetValueOrDefault(s.Key), null, s.Value)));
            }

            case SalesPerformanceGrouping.Zone:
            {
                var sums = await SumsByAsync(invoices, i => i.ZoneId, cancellationToken);
                var wanted = Wanted(sums.Keys);
                var names = await NamesAsync(db.FarmingZones.AsNoTracking().Where(z => wanted.Contains(z.Id)).Select(z => new Named(z.Id, z.Name)), cancellationToken);
                return Sliced(sums.Select(s => new SalesPerformanceSlice(s.Key, names.GetValueOrDefault(s.Key), null, s.Value)));
            }

            case SalesPerformanceGrouping.Product:
                return await ByProductAsync(invoices, cancellationToken);
            default:
            {
                // A week and a month are cut from the days by the calculator: one slice per invoice date is at most a few thousand rows for years.
                var sums = await SumsByAsync(invoices, i => i.InvoiceDate, cancellationToken);
                return Sliced(sums.Select(s => new SalesPerformanceSlice(null, null, s.Key, s.Value)));
            }
        }
    }

    public async Task<IReadOnlyList<SalesPerformanceInvoice>> InvoicesOfGroupAsync(ReportFilter filter, SalesPerformanceGroupFilter group, CancellationToken cancellationToken)
    {
        var invoices = InvoicesOf(filter);
        var (id, from, to) = (group.Id ?? Guid.Empty, group.From ?? DateOnly.MinValue, group.To ?? DateOnly.MaxValue);
        invoices = group.Kind switch
        {
            SalesPerformanceGroupKind.WalkIn => invoices.Where(i => i.CustomerId == null),
            SalesPerformanceGroupKind.Customer => invoices.Where(i => i.CustomerId == id),
            SalesPerformanceGroupKind.Zone => invoices.Where(i => i.ZoneId == id),
            SalesPerformanceGroupKind.DateRange => invoices.Where(i => i.InvoiceDate >= from && i.InvoiceDate <= to),
            SalesPerformanceGroupKind.Product => invoices.Where(i => i.Lines.Any(l => l.ProductId == id)),
            SalesPerformanceGroupKind.Discounts => invoices.Where(i => i.DiscountAmount != 0m),
            SalesPerformanceGroupKind.OtherCharges => invoices.Where(i => i.OtherCharges != 0m),
            _ => invoices.Where(i => i.PriceCutTotal != 0m),
        };
        return await invoices
            .Select(i => new SalesPerformanceInvoice(
                i.Id, i.Code, i.InvoiceDate, i.ZoneId, i.Zone.Code, i.Zone.Name, i.CustomerId, i.Customer == null ? null : i.Customer.Name,
                i.Subtotal, i.DiscountAmount, i.OtherCharges, i.PriceCutTotal, i.Total,
                i.Returns.Where(r => r.StatusId == StatusIds.SalesReturn.Recorded).Sum(r => r.Value),
                i.Returns.Any(r => r.StatusId == StatusIds.SalesReturn.Recorded)))
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// The figures of the invoices added up per key. An invoice has one key, so the total is the sum of the slices. Four grouped commands, each reading the invoices once: the invoices
    /// (count, totals, price cuts), their lines (kg), the returns that stand (value) and the return lines that stand (kg taken back, found by the invoice line they name).
    /// </summary>
    private async Task<Dictionary<TKey, SalesPerformanceSums>> SumsByAsync<TKey>(
        IQueryable<SalesInvoice> invoices, Expression<Func<SalesInvoice, TKey>> key, CancellationToken cancellationToken)
        where TKey : notnull
    {
        var heads = await invoices.GroupBy(key)
            .Select(g => new { g.Key, Count = g.Count(), Total = g.Sum(i => i.Total), Cuts = g.Sum(i => i.PriceCutTotal) })
            .ToListAsync(cancellationToken);
        var kg = await invoices
            .SelectMany(i => i.Lines, (i, l) => new Held<SalesInvoiceLine> { Invoice = i, Item = l })
            .GroupBy(Through<Held<SalesInvoiceLine>, TKey>(h => h.Invoice, key))
            .Select(g => new { g.Key, Kg = g.Sum(h => h.Item.Kg) })
            .ToDictionaryAsync(x => x.Key, x => x.Kg, cancellationToken);
        var returns = await invoices
            .SelectMany(i => i.Returns.Where(r => r.StatusId == StatusIds.SalesReturn.Recorded), (i, r) => new Held<SalesReturn> { Invoice = i, Item = r })
            .GroupBy(Through<Held<SalesReturn>, TKey>(h => h.Invoice, key))
            .Select(g => new { g.Key, Value = g.Sum(h => h.Item.Value) })
            .ToDictionaryAsync(x => x.Key, x => x.Value, cancellationToken);
        var returnedKg = await invoices
            .SelectMany(i => i.Lines, (i, l) => new Held<SalesInvoiceLine> { Invoice = i, Item = l })
            .Join(StandingReturnLines(), h => h.Item.Id, rl => rl.InvoiceLineId, (h, rl) => new Held<SalesReturnLine> { Invoice = h.Invoice, Item = rl })
            .GroupBy(Through<Held<SalesReturnLine>, TKey>(h => h.Invoice, key))
            .Select(g => new { g.Key, Kg = g.Sum(h => h.Item.ReturnedKg) })
            .ToDictionaryAsync(x => x.Key, x => x.Kg, cancellationToken);
        return heads.ToDictionary(
            h => h.Key,
            h =>
            {
                var invoiced = kg.GetValueOrDefault(h.Key);
                return new SalesPerformanceSums(h.Count, invoiced, h.Total, h.Cuts, returns.GetValueOrDefault(h.Key), invoiced - returnedKg.GetValueOrDefault(h.Key));
            });
    }

    /// <summary>
    /// Grouped by product: the rows of the products come from the lines (an invoice counts once for each product on it); the discounts, the other charges and the price cuts of the invoices
    /// are rows of their own, each with its part of the sales returns (the sales return takes back the whole invoice, BR-CLAIM-03).
    /// </summary>
    private async Task<SalesPerformanceAggregates> ByProductAsync(IQueryable<SalesInvoice> invoices, CancellationToken cancellationToken)
    {
        var total = (await SumsByAsync(invoices, i => 1, cancellationToken)).GetValueOrDefault(1) ?? SalesPerformanceSums.None;
        var standing = StatusIds.SalesReturn.Recorded;
        var lines = invoices.SelectMany(i => i.Lines);
        var perProduct = await lines.GroupBy(l => l.ProductId)
            .Select(g => new { ProductId = g.Key, Invoices = g.Select(l => l.InvoiceId).Distinct().Count(), Kg = g.Sum(l => l.Kg), Amount = g.Sum(l => l.Amount) })
            .ToListAsync(cancellationToken);
        var returnedAmount = await invoices.Where(i => i.Returns.Any(r => r.StatusId == standing)).SelectMany(i => i.Lines)
            .GroupBy(l => l.ProductId)
            .Select(g => new { ProductId = g.Key, Amount = g.Sum(l => l.Amount) })
            .ToDictionaryAsync(x => x.ProductId, x => x.Amount, cancellationToken);
        var returnedKg = await lines.Join(StandingReturnLines(), l => l.Id, rl => rl.InvoiceLineId, (l, rl) => new { l.ProductId, rl.ReturnedKg })
            .GroupBy(x => x.ProductId)
            .Select(g => new { ProductId = g.Key, Kg = g.Sum(x => x.ReturnedKg) })
            .ToDictionaryAsync(x => x.ProductId, x => x.Kg, cancellationToken);
        var wantedProducts = Wanted(perProduct.Select(p => p.ProductId));
        var names = await NamesAsync(db.Products.AsNoTracking().Where(p => wantedProducts.Contains(p.Id)).Select(p => new Named(p.Id, p.Name)), cancellationToken);

        var parts = await invoices.GroupBy(i => 1)
            .Select(g => new
            {
                DiscountCount = g.Count(i => i.DiscountAmount != 0m), Discounts = g.Sum(i => i.DiscountAmount),
                OtherCount = g.Count(i => i.OtherCharges != 0m), Other = g.Sum(i => i.OtherCharges),
                CutCount = g.Count(i => i.PriceCutTotal != 0m), Cuts = g.Sum(i => i.PriceCutTotal),
            })
            .SingleOrDefaultAsync(cancellationToken);
        var partsReturned = await invoices.Where(i => i.Returns.Any(r => r.StatusId == standing)).GroupBy(i => 1)
            .Select(g => new { Discounts = g.Sum(i => i.DiscountAmount), Other = g.Sum(i => i.OtherCharges), Cuts = g.Sum(i => i.PriceCutTotal) })
            .SingleOrDefaultAsync(cancellationToken);

        var slices = perProduct.Select(p => new SalesPerformanceSlice(
            p.ProductId, names.GetValueOrDefault(p.ProductId), null,
            new SalesPerformanceSums(
                p.Invoices, p.Kg, p.Amount, 0m, returnedAmount.GetValueOrDefault(p.ProductId), p.Kg - returnedKg.GetValueOrDefault(p.ProductId)))).ToList();
        var sums = parts is null
            ? SalesPerformanceParts.None
            : new SalesPerformanceParts(
                new SalesPerformanceSums(parts.DiscountCount, 0m, -parts.Discounts, 0m, -(partsReturned?.Discounts ?? 0m), 0m),
                new SalesPerformanceSums(parts.OtherCount, 0m, parts.Other, 0m, partsReturned?.Other ?? 0m, 0m),
                new SalesPerformanceSums(parts.CutCount, 0m, -parts.Cuts, parts.Cuts, -(partsReturned?.Cuts ?? 0m), 0m));
        return new SalesPerformanceAggregates(total, slices, sums);
    }

    private IQueryable<SalesReturnLine> StandingReturnLines() =>
        db.SalesReturnLines.AsNoTracking().Where(r => r.Return.StatusId == StatusIds.SalesReturn.Recorded);

    private static SalesPerformanceAggregates Sliced(IEnumerable<SalesPerformanceSlice> slices)
    {
        var list = slices.ToList();
        var total = new SalesPerformanceSums(
            list.Sum(s => s.Sums.InvoiceCount), list.Sum(s => s.Sums.InvoicedKg), list.Sum(s => s.Sums.Revenue), list.Sum(s => s.Sums.PriceCuts), list.Sum(s => s.Sums.Returns),
            list.Sum(s => s.Sums.SoldKg));
        return new SalesPerformanceAggregates(total, list, SalesPerformanceParts.None);
    }

    /// <summary>The names of the customers, zones or products of the slices, read with one command (a name is the same on every invoice of its customer, zone or product).</summary>
    private static async Task<Dictionary<Guid, string>> NamesAsync(IQueryable<Named> source, CancellationToken cancellationToken) =>
        await source.ToDictionaryAsync(n => n.Id, n => n.Name, cancellationToken);

    /// <summary>The ids to look names up for: no walk-in (empty) id, each once.</summary>
    private static List<Guid> Wanted(IEnumerable<Guid> ids) => ids.Where(id => id != Guid.Empty).Distinct().ToList();

    /// <summary>The key of an invoice carried over to a row of one of its children: <c>invoiceOf</c> says where the invoice is in the row.</summary>
    private static Expression<Func<TRow, TKey>> Through<TRow, TKey>(Expression<Func<TRow, SalesInvoice>> invoiceOf, Expression<Func<SalesInvoice, TKey>> key) =>
        Expression.Lambda<Func<TRow, TKey>>(new Substitute(key.Parameters[0], invoiceOf.Body).Visit(key.Body), invoiceOf.Parameters);

    private sealed class Substitute(ParameterExpression parameter, Expression replacement) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == parameter ? replacement : base.VisitParameter(node);
    }

    /// <summary>A row of a child (line, return, return line) with the invoice it belongs to.</summary>
    private sealed class Held<T>
    {
        public SalesInvoice Invoice { get; set; } = null!;
        public T Item { get; set; } = default!;
    }

    private sealed record Named(Guid Id, string Name);

    public async Task<IReadOnlyDictionary<Guid, SalesPerformanceCustomerMoney>> CustomerMoneyAsync(
        IReadOnlyCollection<Guid> customerIds, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        var ids = customerIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return new Dictionary<Guid, SalesPerformanceCustomerMoney>();
        }

        // The debt formula (BR-DEBT-02) over what is dated up to the end of the period: invoices not cancelled, returns that stand, payments, discounts and money paid back not cancelled
        // (a payment still waiting for its transfer counts nowhere) and the debt adjustments not cancelled.
        var invoiceRows = db.SalesInvoices.AsNoTracking()
            .Where(i => i.CustomerId != null && ids.Contains(i.CustomerId.Value) && i.StatusId != StatusIds.SalesInvoice.Cancelled && i.InvoiceDate <= to);
        var standing = db.SalesReturns.AsNoTracking().Where(SalesReturn.Standing);
        var invoiceSums = await (
                from invoice in invoiceRows
                from salesReturn in standing.Where(r => r.InvoiceId == invoice.Id && r.ReturnDate <= to).DefaultIfEmpty()
                group new { invoice.Total, Value = salesReturn == null ? 0m : salesReturn.Value } by invoice.CustomerId!.Value into g
                select new { CustomerId = g.Key, Total = g.Sum(x => x.Total), Returns = g.Sum(x => x.Value) })
            .ToDictionaryAsync(x => x.CustomerId, cancellationToken);
        var payments = db.CustomerPayments.AsNoTracking()
            .Where(p => p.CustomerId != null && ids.Contains(p.CustomerId.Value) && p.StatusId != StatusIds.CustomerPayment.Cancelled && p.StatusId != StatusIds.CustomerPayment.PendingTransfer);
        var upToEnd = await payments.Where(p => p.PaymentDate <= to)
            .GroupBy(p => new { CustomerId = p.CustomerId!.Value, p.Kind })
            .Select(g => new { g.Key.CustomerId, g.Key.Kind, Amount = g.Sum(p => p.Amount), Discount = g.Sum(p => p.DiscountAmount) })
            .ToListAsync(cancellationToken);
        var adjusted = await db.CustomerDebtAdjustments.AsNoTracking()
            .Where(a => a.CancelledAt == null && ids.Contains(a.CustomerId) && a.AdjustmentDate <= to)
            .GroupBy(a => a.CustomerId)
            .Select(g => new { CustomerId = g.Key, Amount = g.Sum(a => a.Amount) })
            .ToDictionaryAsync(x => x.CustomerId, x => x.Amount, cancellationToken);
        // The money of the period itself: by payment date, Completed payments received and money paid back (BR-DEBT-03); Awaiting confirmation is not received yet.
        var inPeriod = await payments.Where(p => p.PaymentDate >= from && p.PaymentDate <= to)
            .GroupBy(p => new { CustomerId = p.CustomerId!.Value, p.Kind, p.StatusId })
            .Select(g => new { g.Key.CustomerId, g.Key.Kind, g.Key.StatusId, Amount = g.Sum(p => p.Amount) })
            .ToListAsync(cancellationToken);

        var toEnd = upToEnd.ToDictionary(p => (p.CustomerId, p.Kind));
        var received = inPeriod.Where(p => p.Kind == CustomerPaymentKind.Payment && p.StatusId == StatusIds.CustomerPayment.Completed)
            .ToDictionary(p => p.CustomerId, p => p.Amount);
        var backInPeriod = inPeriod.Where(p => p.Kind == CustomerPaymentKind.PaidBack).GroupBy(p => p.CustomerId).ToDictionary(g => g.Key, g => g.Sum(p => p.Amount));
        return ids.ToDictionary(id => id, id =>
        {
            invoiceSums.TryGetValue(id, out var sums);
            var paid = toEnd.GetValueOrDefault((id, CustomerPaymentKind.Payment));
            var figures = new CustomerDebtFigures(
                sums?.Total ?? 0m, sums?.Returns ?? 0m, paid?.Amount ?? 0m, toEnd.GetValueOrDefault((id, CustomerPaymentKind.PaidBack))?.Amount ?? 0m, paid?.Discount ?? 0m,
                adjusted.GetValueOrDefault(id));
            return new SalesPerformanceCustomerMoney(figures, received.GetValueOrDefault(id), backInPeriod.GetValueOrDefault(id));
        });
    }

    public async Task<SalesPerformanceUnassignedMoney> UnassignedMoneyAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        // A payment with no customer (a walk-in sale, a transfer held for settlement that was completed) is money received all the same; one waiting for its transfer is not.
        var rows = await db.CustomerPayments.AsNoTracking()
            .Where(p => p.CustomerId == null && p.PaymentDate >= from && p.PaymentDate <= to && p.StatusId != StatusIds.CustomerPayment.Cancelled && p.StatusId != StatusIds.CustomerPayment.PendingTransfer)
            .GroupBy(p => new { p.Kind, p.StatusId })
            .Select(g => new { g.Key.Kind, g.Key.StatusId, Amount = g.Sum(p => p.Amount) })
            .ToListAsync(cancellationToken);
        return new SalesPerformanceUnassignedMoney(
            rows.Where(r => r.Kind == CustomerPaymentKind.Payment && r.StatusId == StatusIds.CustomerPayment.Completed).Sum(r => r.Amount),
            rows.Where(r => r.Kind == CustomerPaymentKind.PaidBack).Sum(r => r.Amount));
    }
    /// <summary>The invoices of the selection: in the period, not Cancelled, of the zones, customers and products chosen.</summary>
    private IQueryable<SalesInvoice> InvoicesOf(ReportFilter filter)
    {
        var (from, to) = (filter.From, filter.To);
        var invoices = db.SalesInvoices.AsNoTracking().Where(i => i.InvoiceDate >= from && i.InvoiceDate <= to && i.StatusId != StatusIds.SalesInvoice.Cancelled);
        if (filter.ZoneIds is { } zoneIds)
        {
            var zones = zoneIds.ToList();
            invoices = invoices.Where(i => zones.Contains(i.ZoneId));
        }

        if (filter.CustomerIds.Count > 0)
        {
            var customers = filter.CustomerIds.ToList();
            invoices = invoices.Where(i => i.CustomerId != null && customers.Contains(i.CustomerId.Value));
        }

        if (filter.ProductIds.Count > 0)
        {
            var products = filter.ProductIds.ToList();
            invoices = invoices.Where(i => i.Lines.Any(l => products.Contains(l.ProductId)));
        }

        return invoices;
    }
}
