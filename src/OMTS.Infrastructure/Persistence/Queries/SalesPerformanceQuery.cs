using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Reports;
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
    public async Task<SalesPerformanceFacts> FactsAsync(ReportFilter filter, CancellationToken cancellationToken)
    {
        var invoices = InvoicesOf(filter);
        var rows = await invoices
            .OrderBy(i => i.InvoiceDate).ThenBy(i => i.Code)
            .Select(i => new SalesPerformanceInvoice(
                i.Id, i.Code, i.InvoiceDate, i.ZoneId, i.Zone.Code, i.Zone.Name, i.CustomerId, i.Customer == null ? null : i.Customer.Name,
                i.Subtotal, i.DiscountAmount, i.OtherCharges, i.PriceCutTotal, i.Total,
                i.Returns.Where(r => r.StatusId == StatusIds.SalesReturn.Recorded).Sum(r => r.Value),
                i.Returns.Any(r => r.StatusId == StatusIds.SalesReturn.Recorded)))
            .ToListAsync(cancellationToken);
        // The kg a return took back are those of the return lines that stand, by the invoice line they name (a return takes back every line in full, BR-CLAIM-03). They are found
        // through the returns of the selected invoices (indexed by invoice), not one look-up per invoice line.
        var returnedKg = await db.SalesReturnLines.AsNoTracking()
            .Where(r => r.Return.StatusId == StatusIds.SalesReturn.Recorded && invoices.Any(i => i.Id == r.Return.InvoiceId))
            .GroupBy(r => r.InvoiceLineId)
            .Select(g => new { InvoiceLineId = g.Key, Kg = g.Sum(r => r.ReturnedKg) })
            .ToDictionaryAsync(x => x.InvoiceLineId, x => x.Kg, cancellationToken);
        var lines = (await invoices
                .SelectMany(i => i.Lines)
                .Select(l => new { l.Id, l.InvoiceId, l.ProductId, ProductName = l.Product.Name, l.Kg, l.Amount })
                .ToListAsync(cancellationToken))
            .Select(l => new SalesPerformanceLine(l.InvoiceId, l.ProductId, l.ProductName, l.Kg, l.Amount, returnedKg.GetValueOrDefault(l.Id)))
            .ToList();
        return new SalesPerformanceFacts(rows, lines);
    }

    public async Task<IReadOnlyDictionary<Guid, SalesPerformanceCustomerMoney>> CustomerMoneyAsync(
        IReadOnlyCollection<Guid> customerIds, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        var ids = customerIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return new Dictionary<Guid, SalesPerformanceCustomerMoney>();
        }

        // The debt formula (BR-DEBT-02) over what is dated up to the end of the period: invoices not cancelled, returns that stand, payments and money paid back not cancelled.
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
            .Where(p => p.CustomerId != null && ids.Contains(p.CustomerId.Value) && p.StatusId != StatusIds.CustomerPayment.Cancelled);
        var upToEnd = await payments.Where(p => p.PaymentDate <= to)
            .GroupBy(p => new { CustomerId = p.CustomerId!.Value, p.Kind })
            .Select(g => new { g.Key.CustomerId, g.Key.Kind, Amount = g.Sum(p => p.Amount) })
            .ToListAsync(cancellationToken);
        // The money of the period itself: by payment date, Confirmed payments received and money paid back (BR-DEBT-03); Awaiting confirmation is not received yet.
        var inPeriod = await payments.Where(p => p.PaymentDate >= from && p.PaymentDate <= to)
            .GroupBy(p => new { CustomerId = p.CustomerId!.Value, p.Kind, p.StatusId })
            .Select(g => new { g.Key.CustomerId, g.Key.Kind, g.Key.StatusId, Amount = g.Sum(p => p.Amount) })
            .ToListAsync(cancellationToken);

        var toEnd = upToEnd.ToDictionary(p => (p.CustomerId, p.Kind), p => p.Amount);
        var received = inPeriod.Where(p => p.Kind == CustomerPaymentKind.Payment && p.StatusId == StatusIds.CustomerPayment.Confirmed)
            .ToDictionary(p => p.CustomerId, p => p.Amount);
        var backInPeriod = inPeriod.Where(p => p.Kind == CustomerPaymentKind.PaidBack).GroupBy(p => p.CustomerId).ToDictionary(g => g.Key, g => g.Sum(p => p.Amount));
        return ids.ToDictionary(id => id, id =>
        {
            invoiceSums.TryGetValue(id, out var sums);
            var figures = new CustomerDebtFigures(
                sums?.Total ?? 0m, sums?.Returns ?? 0m, toEnd.GetValueOrDefault((id, CustomerPaymentKind.Payment)), toEnd.GetValueOrDefault((id, CustomerPaymentKind.PaidBack)));
            return new SalesPerformanceCustomerMoney(figures, received.GetValueOrDefault(id), backInPeriod.GetValueOrDefault(id));
        });
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
