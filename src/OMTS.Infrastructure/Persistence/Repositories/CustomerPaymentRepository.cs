using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Infrastructure.Persistence.Repositories;

internal sealed class CustomerPaymentRepository(OmtsDbContext db) : ICustomerPaymentRepository
{
    public void Add(CustomerPayment payment) => db.CustomerPayments.Add(payment);

    public Task<CustomerPayment?> GetForEditAsync(Guid paymentId, CancellationToken cancellationToken) =>
        db.CustomerPayments.Include(p => p.Customer).Include(p => p.Invoice).FirstOrDefaultAsync(p => p.Id == paymentId, cancellationToken);

    public async Task<IReadOnlyList<CustomerPayment>> GetForEditAsync(IReadOnlyCollection<Guid> paymentIds, CancellationToken cancellationToken)
    {
        var ids = paymentIds.Distinct().ToList();
        return ids.Count == 0
            ? []
            : await db.CustomerPayments.Include(p => p.Customer).Include(p => p.Invoice).Where(p => ids.Contains(p.Id)).ToListAsync(cancellationToken);
    }

    public Task<CustomerPayment?> GetAsync(Guid paymentId, CancellationToken cancellationToken) =>
        db.CustomerPayments.AsNoTracking().Include(p => p.Customer).Include(p => p.Invoice).FirstOrDefaultAsync(p => p.Id == paymentId, cancellationToken);

    public Task<CustomerPayment?> FindByDeduplicationKeyAsync(string key, CancellationToken cancellationToken) =>
        db.CustomerPayments.AsNoTracking().Include(p => p.Customer).Include(p => p.Invoice).FirstOrDefaultAsync(p => p.DeduplicationKey == key, cancellationToken);

    public Task<CustomerPayment?> FindNotCancelledByBankReferenceAsync(string bankReference, CancellationToken cancellationToken) =>
        db.CustomerPayments.AsNoTracking().Include(p => p.Customer)
            .Where(p => p.BankReference == bankReference && p.StatusId != StatusIds.CustomerPayment.Cancelled)
            .OrderByDescending(p => p.PaymentDate).ThenByDescending(p => p.Code)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<(IReadOnlyList<CustomerPayment> Items, int Total, decimal TotalAmount)> AwaitingConfirmationAsync(
        int page, int pageSize, CancellationToken cancellationToken)
    {
        var awaiting = db.CustomerPayments.AsNoTracking().Where(p => p.StatusId == StatusIds.CustomerPayment.AwaitingConfirmation);
        var total = await awaiting.CountAsync(cancellationToken);
        var amount = total == 0 ? 0m : await awaiting.SumAsync(p => p.Amount, cancellationToken);
        var items = await awaiting.Include(p => p.Customer).Include(p => p.Invoice)
            .OrderBy(p => p.PaymentDate).ThenBy(p => p.Code)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(cancellationToken);
        return (items, total, amount);
    }
}
