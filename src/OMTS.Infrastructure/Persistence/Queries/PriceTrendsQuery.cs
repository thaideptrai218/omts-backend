using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Reports;
using OMTS.Domain.Constants;

namespace OMTS.Infrastructure.Persistence.Queries;

/// <summary>
/// REPORT-02 (BR-REPORT-02) on the indexes of the invoices (InvoiceDate, ZoneId): the lines of the invoices in the period (by invoice date, the business date UTC+7) that are not Cancelled and
/// have no sales return that stands, at the latest values of the invoice. A return that was cancelled does not count (BR-CLAIM-03), so its invoice is counted again. Read only; one command.
/// </summary>
internal sealed class PriceTrendsQuery(OmtsDbContext db) : IPriceTrendsQuery
{
    public async Task<IReadOnlyList<PriceTrendLine>> LinesAsync(ReportFilter filter, CancellationToken cancellationToken)
    {
        var (from, to) = (filter.From, filter.To);
        var invoices = db.SalesInvoices.AsNoTracking().Where(i =>
            i.InvoiceDate >= from && i.InvoiceDate <= to && i.StatusId != StatusIds.SalesInvoice.Cancelled
            && !i.Returns.Any(r => r.StatusId == StatusIds.SalesReturn.Recorded));
        if (filter.ZoneIds is { } zoneIds)
        {
            var zones = zoneIds.ToList();
            invoices = invoices.Where(i => zones.Contains(i.ZoneId));
        }

        var lines = invoices.SelectMany(i => i.Lines);
        if (filter.ProductIds.Count > 0)
        {
            var products = filter.ProductIds.ToList();
            lines = lines.Where(l => products.Contains(l.ProductId));
        }

        return await lines
            .Select(l => new PriceTrendLine(
                l.InvoiceId, l.Invoice.Code, l.Invoice.InvoiceDate, l.Invoice.CustomerId, l.Invoice.Customer == null ? null : l.Invoice.Customer.Name,
                l.ProductId, l.Product.Name, l.Kg, l.UnitPrice, l.Amount))
            .ToListAsync(cancellationToken);
    }
}
