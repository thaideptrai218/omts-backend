using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Reports;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Infrastructure.Persistence.Queries;

/// <summary>
/// REPORT-03 (BR-REPORT-03): each event by its own date. Claims by the time they were sent, sales returns that stand (not cancelled) by their return date, price cuts by their date
/// (<see cref="SalesInvoicePriceCut"/>: the only place a price cut of SALE-07 is kept, so a correction of a data-entry error is never read) and cancelled invoices by their cancel date; the
/// dates of an instant are business dates (UTC+7). The zone and the customer are those of the invoice, so the invoice date does not matter. Read only; a fixed number of commands.
/// </summary>
internal sealed class PostSaleIssuesQuery(OmtsDbContext db) : IPostSaleIssuesQuery
{
    public async Task<PostSaleFacts> FactsAsync(ReportFilter filter, CancellationToken cancellationToken)
    {
        var (firstDay, lastDay) = (filter.From, filter.To);
        // The instants of the first and the last business day, so the filter on them uses the index of the date.
        var start = new DateTimeOffset(firstDay.ToDateTime(TimeOnly.MinValue), BusinessClock.UtcOffset);
        var end = new DateTimeOffset(lastDay.AddDays(1).ToDateTime(TimeOnly.MinValue), BusinessClock.UtcOffset);
        var invoices = InvoicesOf(filter);

        var claims = await (
                from claim in db.QualityClaims.AsNoTracking().Where(x => x.SentAt >= start && x.SentAt < end)
                join i in invoices on claim.InvoiceId equals i.Id
                select new
                {
                    claim.Id, At = claim.SentAt, claim.StatusId, claim.Description, Invoice = i.Id, i.Code, i.ZoneId, ZoneName = i.Zone.Name, i.CustomerId,
                    CustomerName = i.Customer == null ? null : i.Customer.Name,
                })
            .ToListAsync(cancellationToken);
        var returns = await (
                from salesReturn in db.SalesReturns.AsNoTracking().Where(SalesReturn.Standing).Where(x => x.ReturnDate >= firstDay && x.ReturnDate <= lastDay)
                join i in invoices on salesReturn.InvoiceId equals i.Id
                select new
                {
                    salesReturn.Id, salesReturn.ReturnDate, salesReturn.Reason, salesReturn.Value, Invoice = i.Id, i.Code, i.ZoneId, ZoneName = i.Zone.Name, i.CustomerId,
                    CustomerName = i.Customer == null ? null : i.Customer.Name,
                })
            .ToListAsync(cancellationToken);
        var priceCuts = await (
                from cut in db.SalesInvoicePriceCuts.AsNoTracking().Where(x => x.CutDate >= firstDay && x.CutDate <= lastDay)
                join i in invoices on cut.InvoiceId equals i.Id
                select new
                {
                    cut.Id, cut.CutDate, cut.Reason, cut.Amount, Invoice = i.Id, i.Code, i.ZoneId, ZoneName = i.Zone.Name, i.CustomerId,
                    CustomerName = i.Customer == null ? null : i.Customer.Name,
                })
            .ToListAsync(cancellationToken);
        var cancelled = await invoices
            .Where(i => i.StatusId == StatusIds.SalesInvoice.Cancelled && i.CancelledAt >= start && i.CancelledAt < end)
            .Select(i => new
            {
                i.Id, At = i.CancelledAt!.Value, i.CancelReason, i.Total, i.Code, i.ZoneId, ZoneName = i.Zone.Name, i.CustomerId, CustomerName = i.Customer == null ? null : i.Customer.Name,
            })
            .ToListAsync(cancellationToken);

        var events = new List<PostSaleEvent>();
        events.AddRange(claims.Select(c => new PostSaleEvent(
            PostSaleEventType.Claim, c.Id, BusinessClock.ToBusinessDate(c.At), c.Invoice, c.Code, c.ZoneId, c.ZoneName, c.CustomerId, c.CustomerName, c.Description, null, c.StatusId)));
        events.AddRange(returns.Select(r => new PostSaleEvent(
            PostSaleEventType.Return, r.Id, r.ReturnDate, r.Invoice, r.Code, r.ZoneId, r.ZoneName, r.CustomerId, r.CustomerName, r.Reason, r.Value, null)));
        events.AddRange(priceCuts.Select(p => new PostSaleEvent(
            PostSaleEventType.PriceCut, p.Id, p.CutDate, p.Invoice, p.Code, p.ZoneId, p.ZoneName, p.CustomerId, p.CustomerName, p.Reason, p.Amount, null)));
        events.AddRange(cancelled.Select(i => new PostSaleEvent(
            PostSaleEventType.Cancellation, i.Id, BusinessClock.ToBusinessDate(i.At), i.Id, i.Code, i.ZoneId, i.ZoneName, i.CustomerId, i.CustomerName, i.CancelReason, i.Total, null)));

        // The lots they concern: of the lines of the invoices, and of the return lines of the returns (the kg that came back, and the kg thrown away).
        var invoiceIds = events.Select(e => e.InvoiceId).Distinct().ToList();
        var returnIds = returns.Select(r => r.Id).ToList();
        var invoiceLots = new List<PostSaleInvoiceLot>();
        if (invoiceIds.Count > 0)
        {
            // A lot on two lines of an invoice is one lot of it: the records compare by value.
            var lines = await db.SalesInvoiceLines.AsNoTracking().Where(l => invoiceIds.Contains(l.InvoiceId))
                .Select(l => new PostSaleInvoiceLot(l.InvoiceId, l.HarvestLotId, l.HarvestLot.LotCode)).ToListAsync(cancellationToken);
            invoiceLots = lines.Distinct().ToList();
        }

        var returnLots = new List<PostSaleReturnLot>();
        if (returnIds.Count > 0)
        {
            returnLots = await db.SalesReturnLines.AsNoTracking().Where(l => returnIds.Contains(l.ReturnId))
                .Select(l => new PostSaleReturnLot(l.ReturnId, l.HarvestLotId, l.HarvestLot.LotCode, l.ReturnedKg, l.DiscardedKg)).ToListAsync(cancellationToken);
        }

        return new PostSaleFacts(events, invoiceLots, returnLots);
    }

    /// <summary>The invoices of the zones and the customers chosen, whatever their date or status: an event of a cancelled invoice still happened.</summary>
    private IQueryable<SalesInvoice> InvoicesOf(ReportFilter filter)
    {
        var invoices = db.SalesInvoices.AsNoTracking();
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

        return invoices;
    }
}
