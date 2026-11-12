using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Notifications;
using OMTS.Application.Features.Sales.Invoices;
using OMTS.Domain.Constants;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Persistence.Configurations;

namespace OMTS.Infrastructure.Persistence.Queries;

/// <summary>SALE-09 on the indexes (InvoiceDate, ZoneId), lines by lot, payments by invoice and Notifications by related record: no index of its own and no query per invoice.</summary>
internal sealed class SalesInvoiceSearchReader(OmtsDbContext db) : ISalesInvoiceSearchReader
{
    public async Task<IReadOnlyList<SalesInvoiceSearchKey>> MatchingAsync(SalesInvoiceSearchFilter filter, CancellationToken cancellationToken)
    {
        var query = db.SalesInvoices.AsNoTracking().Where(i => i.InvoiceDate >= filter.From && i.InvoiceDate <= filter.To);
        if (filter.ZoneIds is { } zoneIds)
        {
            var ids = zoneIds.ToList();
            query = query.Where(i => ids.Contains(i.ZoneId));
        }

        if (filter.CustomerId is { } customerId)
        {
            query = query.Where(i => i.CustomerId == customerId);
        }

        if (filter.SalesOrderId is { } orderId)
        {
            query = query.Where(i => i.SalesOrderId == orderId);
        }

        if (filter.HarvestLotId is { } lotId)
        {
            query = query.Where(i => i.Lines.Any(l => l.HarvestLotId == lotId));
        }

        if (filter.RaftId is { } raftId)
        {
            query = query.Where(i => i.Lines.Any(l => l.HarvestLot.RaftId == raftId));
        }

        if (filter.StatusId is { } statusId)
        {
            query = query.Where(i => i.StatusId == statusId);
        }

        if (!string.IsNullOrWhiteSpace(filter.Code))
        {
            // A text longer than the column cannot match, and a very long parameter is not something to send to the database. Otherwise translated with the wildcards as plain text.
            var code = filter.Code.Trim();
            if (code.Length > Lengths.Code)
            {
                return [];
            }

            query = query.Where(i => i.Code.Contains(code));
        }

        if (filter.CashCollectedBy is { } collector)
        {
            query = query.Where(i => i.Payments.Any(p => p.CollectedByAccountId == collector && p.Method == CustomerPaymentMethod.Cash && p.Kind == CustomerPaymentKind.Payment
                && p.StatusId == StatusIds.CustomerPayment.AwaitingConfirmation));
        }

        return await query
            .OrderByDescending(i => i.InvoicedAt).ThenByDescending(i => i.Code)
            .Select(i => new SalesInvoiceSearchKey(
                i.Id,
                i.CustomerId,
                i.Total,
                i.StatusId,
                i.CustomerId != null
                    ? 0m
                    : i.Payments.Where(p => p.Kind == CustomerPaymentKind.Payment && p.StatusId != StatusIds.CustomerPayment.Cancelled).Sum(p => (decimal?)p.Amount) ?? 0m))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SalesInvoiceSearchRow>> RowsAsync(IReadOnlyCollection<Guid> invoiceIds, CancellationToken cancellationToken)
    {
        if (invoiceIds.Count == 0)
        {
            return [];
        }

        var ids = invoiceIds.ToList();
        return await db.SalesInvoices.AsNoTracking()
            .Where(i => ids.Contains(i.Id))
            .Select(i => new SalesInvoiceSearchRow(
                i.Id, i.Code, i.InvoicedAt, i.InvoiceDate, i.ZoneId, i.Zone.Code, i.Zone.Name,
                i.CustomerId, i.Customer == null ? null : i.Customer.Name, i.Customer == null ? null : i.Customer.PhoneNumber, i.Total, i.StatusId, i.PaymentTerm,
                i.Customer == null ? null : i.Customer.Code, i.SalesOrderId, i.SalesOrder == null ? null : i.SalesOrder.Code))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<Guid, SalesInvoiceLatestSending>> LatestSendingsAsync(IReadOnlyCollection<Guid> invoiceIds, CancellationToken cancellationToken)
    {
        if (invoiceIds.Count == 0)
        {
            return new Dictionary<Guid, SalesInvoiceLatestSending>();
        }

        // The invoice message only, as the detail reads it: the cancellation notice (SALE-08) is not a sending of the invoice.
        var ids = invoiceIds.ToList();
        var sendings = await db.Notifications.AsNoTracking()
            .Where(n => n.RelatedRecordType == RelatedRecordTypes.SalesInvoice && n.RelatedRecordId != null && ids.Contains(n.RelatedRecordId.Value)
                && n.Type == SalesInvoiceMessages.MessageType && n.Channel != NotificationChannel.InApp)
            .Select(n => new { InvoiceId = n.RelatedRecordId!.Value, n.DeliveryStatusId, n.DeliveryStatusAt, n.CreatedAt })
            .ToListAsync(cancellationToken);
        return sendings
            .GroupBy(s => s.InvoiceId)
            .ToDictionary(g => g.Key, g =>
            {
                var latest = g.MaxBy(s => s.CreatedAt)!;
                return new SalesInvoiceLatestSending(latest.DeliveryStatusId, latest.DeliveryStatusAt);
            });
    }

    public async Task<IReadOnlyDictionary<Guid, decimal>> CashAwaitingConfirmationAsync(
        IReadOnlyCollection<Guid> invoiceIds, Guid collectorAccountId, CancellationToken cancellationToken)
    {
        if (invoiceIds.Count == 0)
        {
            return new Dictionary<Guid, decimal>();
        }

        var ids = invoiceIds.ToList();
        return await db.CustomerPayments.AsNoTracking()
            .Where(p => p.InvoiceId != null && ids.Contains(p.InvoiceId.Value) && p.CollectedByAccountId == collectorAccountId && p.Method == CustomerPaymentMethod.Cash
                && p.Kind == CustomerPaymentKind.Payment && p.StatusId == StatusIds.CustomerPayment.AwaitingConfirmation)
            .GroupBy(p => p.InvoiceId!.Value)
            .Select(g => new { InvoiceId = g.Key, Amount = g.Sum(p => p.Amount) })
            .ToDictionaryAsync(x => x.InvoiceId, x => x.Amount, cancellationToken);
    }
}
