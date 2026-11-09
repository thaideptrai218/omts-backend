using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Models.Sales;
using OMTS.Application.Features.Notifications;
using OMTS.Application.Features.Sales.Invoices;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Infrastructure.Persistence.Repositories;

internal sealed class SalesInvoiceRepository(OmtsDbContext db) : ISalesInvoiceRepository
{
    public void Add(SalesInvoice invoice) => db.SalesInvoices.Add(invoice);

    public void AddPayment(CustomerPayment payment) => db.CustomerPayments.Add(payment);

    public void AddLine(SalesInvoiceLine line) => db.SalesInvoiceLines.Add(line);

    public void RemoveLine(SalesInvoiceLine line) => db.SalesInvoiceLines.Remove(line);

    public void AddPriceCut(SalesInvoicePriceCut priceCut) => db.Add(priceCut);

    public async Task<Guid?> FindByDeduplicationKeyAsync(string key, CancellationToken cancellationToken) =>
        await db.SalesInvoices.AsNoTracking().Where(i => i.DeduplicationKey == key).Select(i => (Guid?)i.Id).FirstOrDefaultAsync(cancellationToken);

    public Task<SalesInvoiceRef?> FindByCodeAsync(string code, CancellationToken cancellationToken) =>
        db.SalesInvoices.AsNoTracking().Where(i => i.Code == code).Select(i => new SalesInvoiceRef(i.Id, i.Code, i.CustomerId)).FirstOrDefaultAsync(cancellationToken);

    public Task<SalesInvoice?> GetAsync(Guid invoiceId, CancellationToken cancellationToken) =>
        db.SalesInvoices.AsNoTracking().AsSplitQuery()
            .Include(i => i.Zone)
            .Include(i => i.Customer)
            .Include(i => i.PurchaseRequest)
            .Include(i => i.Lines).ThenInclude(l => l.Product)
            .Include(i => i.Lines).ThenInclude(l => l.HarvestLot)
            .Include(i => i.PortWeighing).ThenInclude(w => w!.Cages)
            .Include(i => i.PriceCuts)
            .Include(i => i.Payments)
            .Include(i => i.Returns)
            .FirstOrDefaultAsync(i => i.Id == invoiceId, cancellationToken);

    public async Task<(IReadOnlyList<CustomerInvoiceRow> Rows, int Total)> ListOfCustomerAsync(Guid customerId, int page, int pageSize, CancellationToken cancellationToken)
    {
        var ofCustomer = db.SalesInvoices.AsNoTracking().Where(i => i.CustomerId == customerId);
        var total = await ofCustomer.CountAsync(cancellationToken);
        var rows = await ofCustomer
            .OrderByDescending(i => i.InvoicedAt).ThenByDescending(i => i.Code)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(i => new CustomerInvoiceRow(
                i.Id, i.Code, i.InvoicedAt, i.InvoiceDate, i.Zone.Name, i.PurchaseRequest != null ? i.PurchaseRequest.Code : null,
                i.PortWeighing != null ? i.PortWeighing.Cages.Count : 0, i.Lines.Sum(l => l.Kg), i.Total, i.StatusId, i.PaymentTerm))
            .ToListAsync(cancellationToken);
        return (rows, total);
    }

    public async Task<IReadOnlyDictionary<Guid, int>> DeliveredCagesAsync(IReadOnlyCollection<Guid> requestIds, CancellationToken cancellationToken) =>
        await db.PortWeighingCages.AsNoTracking()
            .Where(c => requestIds.Contains(c.PortWeighing.PurchaseRequestId) && c.PortWeighing.Invoices.Any(i => i.StatusId == StatusIds.SalesInvoice.Completed))
            .GroupBy(c => c.PortWeighing.PurchaseRequestId)
            .Select(g => new { RequestId = g.Key, Cages = g.Count() })
            .ToDictionaryAsync(x => x.RequestId, x => x.Cages, cancellationToken);

    public Task<SalesInvoice?> GetForSendingAsync(Guid invoiceId, CancellationToken cancellationToken) =>
        db.SalesInvoices.AsSplitQuery()
            .Include(i => i.Customer)
            .Include(i => i.Lines)
            .Include(i => i.Payments)
            .Include(i => i.Returns)
            .FirstOrDefaultAsync(i => i.Id == invoiceId, cancellationToken);

    public async Task<IReadOnlyList<SalesInvoiceSending>> ListSendingsAsync(Guid invoiceId, CancellationToken cancellationToken) =>
        await db.Notifications.AsNoTracking()
            // The invoice message only: another message to the trader's phone about the invoice (the cancellation notice of SALE-08, PR 10) is not one of its sendings.
            .Where(n => n.RelatedRecordType == RelatedRecordTypes.SalesInvoice && n.RelatedRecordId == invoiceId && n.Type == SalesInvoiceMessages.MessageType
                && n.Channel != NotificationChannel.InApp)
            .OrderBy(n => n.CreatedAt)
            .Select(n => new SalesInvoiceSending(n.Channel, n.PhoneNumber, n.DeliveryStatusId, n.DeliveryStatusAt, n.CreatedAt))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<SalesInvoiceCorrectionEvent>> ListCorrectionEventsAsync(IReadOnlyCollection<Guid> invoiceIds, CancellationToken cancellationToken)
    {
        var ids = invoiceIds.Distinct().ToList();
        return ids.Count == 0
            ? []
            : await db.AuditEvents.AsNoTracking()
                .Where(e => e.Action == SalesInvoiceCorrectionWriter.CorrectedAction && e.RecordId != null && ids.Contains(e.RecordId.Value))
                .OrderBy(e => e.OccurredAt)
                .Select(e => new SalesInvoiceCorrectionEvent(e.RecordId!.Value, e.OccurredAt, e.Reason, e.After))
                .ToListAsync(cancellationToken);
    }
}
