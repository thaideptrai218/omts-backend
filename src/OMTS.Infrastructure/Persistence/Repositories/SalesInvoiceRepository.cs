using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Infrastructure.Persistence.Repositories;

internal sealed class SalesInvoiceRepository(OmtsDbContext db) : ISalesInvoiceRepository
{
    public void Add(SalesInvoice invoice) => db.SalesInvoices.Add(invoice);

    public void AddPayment(CustomerPayment payment) => db.CustomerPayments.Add(payment);

    public async Task<Guid?> FindByDeduplicationKeyAsync(string key, CancellationToken cancellationToken) =>
        await db.SalesInvoices.AsNoTracking().Where(i => i.DeduplicationKey == key).Select(i => (Guid?)i.Id).FirstOrDefaultAsync(cancellationToken);

    public Task<SalesInvoice?> GetAsync(Guid invoiceId, CancellationToken cancellationToken) =>
        db.SalesInvoices.AsNoTracking().AsSplitQuery()
            .Include(i => i.Zone)
            .Include(i => i.Customer)
            .Include(i => i.Lines).ThenInclude(l => l.Product)
            .Include(i => i.Lines).ThenInclude(l => l.HarvestLot)
            .Include(i => i.Payments)
            .FirstOrDefaultAsync(i => i.Id == invoiceId, cancellationToken);

    public Task<SalesInvoice?> GetForSendingAsync(Guid invoiceId, CancellationToken cancellationToken) =>
        db.SalesInvoices.AsSplitQuery()
            .Include(i => i.Customer)
            .Include(i => i.Lines)
            .Include(i => i.Payments)
            .FirstOrDefaultAsync(i => i.Id == invoiceId, cancellationToken);

    public async Task<IReadOnlyList<SalesInvoiceSending>> ListSendingsAsync(Guid invoiceId, CancellationToken cancellationToken) =>
        await db.Notifications.AsNoTracking()
            .Where(n => n.RelatedRecordType == RelatedRecordTypes.SalesInvoice && n.RelatedRecordId == invoiceId && n.Channel != NotificationChannel.InApp)
            .OrderBy(n => n.CreatedAt)
            .Select(n => new SalesInvoiceSending(n.Channel, n.PhoneNumber, n.DeliveryStatusId, n.DeliveryStatusAt, n.CreatedAt))
            .ToListAsync(cancellationToken);
}
