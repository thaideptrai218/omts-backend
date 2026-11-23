using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Infrastructure.Persistence.Repositories;

internal sealed class TraderCheckoutRepository(OmtsDbContext db) : ITraderCheckoutRepository
{
    private IQueryable<TraderCheckout> WithPayment =>
        db.TraderCheckouts.Include(c => c.Payment).ThenInclude(p => p.CashAccount).Include(c => c.Invoice);

    public void Add(TraderCheckout checkout) => db.TraderCheckouts.Add(checkout);

    public Task<TraderCheckout?> GetForEditAsync(Guid checkoutId, CancellationToken cancellationToken) =>
        WithPayment.FirstOrDefaultAsync(c => c.Id == checkoutId, cancellationToken);

    public Task<TraderCheckout?> GetAsync(Guid checkoutId, CancellationToken cancellationToken) =>
        WithPayment.AsNoTracking().FirstOrDefaultAsync(c => c.Id == checkoutId, cancellationToken);

    public Task<TraderCheckout?> FindByOrderCodeAsync(long orderCode, CancellationToken cancellationToken) =>
        WithPayment.FirstOrDefaultAsync(c => c.PayOsOrderCode == orderCode, cancellationToken);

    public Task<bool> OrderCodeTakenAsync(long orderCode, CancellationToken cancellationToken) =>
        db.TraderCheckouts.AnyAsync(c => c.PayOsOrderCode == orderCode, cancellationToken);

    public Task<TraderCheckout?> FindPendingOfInvoiceAsync(Guid invoiceId, CancellationToken cancellationToken) =>
        WithPayment
            .Where(c => c.InvoiceId == invoiceId && c.Payment.StatusId == StatusIds.CustomerPayment.PendingTransfer)
            .OrderByDescending(c => c.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<Guid>> ListToReportAsync(DateTimeOffset lastAttemptBefore, int maxAttempts, int take, CancellationToken cancellationToken) =>
        await db.TraderCheckouts.AsNoTracking()
            .Where(c => c.ZaloOrderId != null && c.ZaloReportedAt == null && c.ZaloReportAttempts < maxAttempts
                && (c.ZaloLastAttemptAt == null || c.ZaloLastAttemptAt < lastAttemptBefore)
                && c.Payment.StatusId == StatusIds.CustomerPayment.Completed && c.Payment.Amount >= c.Amount)
            .OrderBy(c => c.CreatedAt)
            .Select(c => c.Id)
            .Take(take)
            .ToListAsync(cancellationToken);
}
