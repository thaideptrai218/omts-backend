using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Constants;
using OMTS.Domain.Enums;

namespace OMTS.Infrastructure.Persistence.Queries;

/// <summary>
/// The end-of-day sales read: the invoices of the day (not Cancelled), the sales returns that stand dated that day, the kg per product of those invoices and the lots weighed that day.
/// Read only; a fixed number of commands.
/// </summary>
internal sealed class EndOfDayReportQuery(OmtsDbContext db) : IEndOfDayReportQuery
{
    public async Task<EndOfDayFacts> FactsAsync(DateOnly date, IReadOnlyCollection<Guid>? zoneIds, Guid? createdBy, CancellationToken cancellationToken)
    {
        var zones = zoneIds?.ToList();
        var invoices = db.SalesInvoices.AsNoTracking().Where(i => i.InvoiceDate == date && i.StatusId != StatusIds.SalesInvoice.Cancelled);
        var returns = db.SalesReturns.AsNoTracking().Where(r => r.ReturnDate == date && r.StatusId == StatusIds.SalesReturn.Recorded);
        var lots = db.HarvestLots.AsNoTracking().Where(l => l.WeighedOn == date && l.WeighedKg != null);
        if (zones is not null)
        {
            invoices = invoices.Where(i => zones.Contains(i.ZoneId));
            returns = returns.Where(r => zones.Contains(r.Invoice.ZoneId));
            lots = lots.Where(l => zones.Contains(l.Raft.ZoneId));
        }

        if (createdBy is { } account)
        {
            invoices = invoices.Where(i => i.CreatedByAccountId == account);
            returns = returns.Where(r => r.RecordedByAccountId == account);
        }

        var paid = await db.CustomerPayments.AsNoTracking()
            .Where(p => p.InvoiceId != null && p.PaymentDate == date && p.Kind == CustomerPaymentKind.Payment
                && p.StatusId != StatusIds.CustomerPayment.Cancelled && p.StatusId != StatusIds.CustomerPayment.PendingTransfer && invoices.Any(i => i.Id == p.InvoiceId))
            .GroupBy(p => p.InvoiceId!.Value)
            .Select(g => new { InvoiceId = g.Key, Amount = g.Sum(p => p.Amount) })
            .ToDictionaryAsync(x => x.InvoiceId, x => x.Amount, cancellationToken);
        var invoiceRows = (await invoices
                .OrderBy(i => i.InvoicedAt).ThenBy(i => i.Code)
                .Select(i => new { i.Id, i.Code, i.InvoicedAt, i.CustomerId, CustomerName = i.Customer == null ? null : i.Customer.Name, i.ZoneId, ZoneName = i.Zone.Name, Kg = i.Lines.Sum(l => l.Kg), i.Total })
                .ToListAsync(cancellationToken))
            .Select(i => new EndOfDayInvoice(i.Id, i.Code, i.InvoicedAt, i.CustomerId, i.CustomerName, i.ZoneId, i.ZoneName, i.Kg, i.Total, paid.GetValueOrDefault(i.Id)))
            .ToList();
        var returnRows = await returns
            .OrderBy(r => r.Code)
            .Select(r => new EndOfDayReturn(r.Id, r.Code, r.InvoiceId, r.Invoice.Code, r.Invoice.Customer == null ? null : r.Invoice.Customer.Name, r.Invoice.ZoneId, r.Invoice.Zone.Name, r.Value))
            .ToListAsync(cancellationToken);
        var products = (await invoices.SelectMany(i => i.Lines)
                .GroupBy(l => new { l.ProductId, l.Product.Code, l.Product.Name })
                .Select(g => new { g.Key.ProductId, g.Key.Code, g.Key.Name, Kg = g.Sum(l => l.Kg) })
                .ToListAsync(cancellationToken))
            .OrderByDescending(p => p.Kg).ThenBy(p => p.Name, StringComparer.CurrentCulture)
            .Select(p => new EndOfDayProduct(p.ProductId, p.Code, p.Name, p.Kg))
            .ToList();
        var lotRows = (await lots
                .OrderBy(l => l.WeighedAt)
                .Select(l => new { l.Id, l.LotCode, l.Raft.ZoneId, ZoneName = l.Raft.Zone.Name, Kg = l.WeighedKg!.Value, At = l.WeighedAt })
                .ToListAsync(cancellationToken))
            .Select(l => new EndOfDayLot(l.Id, l.LotCode, l.ZoneId, l.ZoneName, l.Kg, l.At ?? default))
            .ToList();
        return new EndOfDayFacts(invoiceRows, returnRows, products, lotRows);
    }
}
