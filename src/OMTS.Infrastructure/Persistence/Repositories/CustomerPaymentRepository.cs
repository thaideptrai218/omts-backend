using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Infrastructure.Persistence.Repositories;

internal sealed class CustomerPaymentRepository(OmtsDbContext db) : ICustomerPaymentRepository
{
    public void Add(CustomerPayment payment) => db.CustomerPayments.Add(payment);

    public void RemoveAllocation(CustomerPaymentAllocation allocation) => db.CustomerPaymentAllocations.Remove(allocation);

    public async Task<Guid?> FindIdByCodeAsync(string code, Guid? exceptPaymentId, CancellationToken cancellationToken)
    {
        var upper = code.ToUpperInvariant();
        return await db.CustomerPayments.AsNoTracking().Where(p => p.Code.ToUpper() == upper && p.Id != exceptPaymentId).Select(p => (Guid?)p.Id).FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<CustomerPayment>> GetPendingTransfersAsync(CancellationToken cancellationToken) =>
        await With(db.CustomerPayments).Where(p => p.StatusId == StatusIds.CustomerPayment.PendingTransfer).OrderBy(p => p.PaymentDate).ThenBy(p => p.Code).ToListAsync(cancellationToken);

    /// <summary>The customer, the invoice, the order of an advance, the bank account and the parts of the payment, with the invoices they name.</summary>
    private static IQueryable<CustomerPayment> With(IQueryable<CustomerPayment> payments) =>
        payments.Include(p => p.Customer).Include(p => p.Invoice).Include(p => p.CashAccount).Include(p => p.SalesOrder).Include(p => p.Allocations).ThenInclude(a => a.Invoice);

    public Task<CustomerPayment?> GetForEditAsync(Guid paymentId, CancellationToken cancellationToken) =>
        With(db.CustomerPayments).FirstOrDefaultAsync(p => p.Id == paymentId, cancellationToken);

    public async Task<IReadOnlyList<CustomerPayment>> GetForEditAsync(IReadOnlyCollection<Guid> paymentIds, CancellationToken cancellationToken)
    {
        var ids = paymentIds.Distinct().ToList();
        return ids.Count == 0
            ? []
            : await With(db.CustomerPayments).Where(p => ids.Contains(p.Id)).ToListAsync(cancellationToken);
    }

    public Task<CustomerPayment?> GetAsync(Guid paymentId, CancellationToken cancellationToken) =>
        With(db.CustomerPayments.AsNoTracking()).FirstOrDefaultAsync(p => p.Id == paymentId, cancellationToken);

    public Task<CustomerPayment?> FindByDeduplicationKeyAsync(string key, CancellationToken cancellationToken) =>
        With(db.CustomerPayments.AsNoTracking()).FirstOrDefaultAsync(p => p.DeduplicationKey == key, cancellationToken);

    public Task<CustomerPayment?> FindNotCancelledByBankReferenceAsync(string bankReference, CancellationToken cancellationToken) =>
        db.CustomerPayments.AsNoTracking().Include(p => p.Customer)
            .Where(p => p.BankReference == bankReference && p.StatusId != StatusIds.CustomerPayment.Cancelled)
            .OrderByDescending(p => p.PaymentDate).ThenByDescending(p => p.Code)
            .FirstOrDefaultAsync(cancellationToken);

    public Task<CustomerPayment?> FindRecordedTransferAsync(string bankReference, CancellationToken cancellationToken)
    {
        var reference = bankReference.Trim();
        return db.CustomerPayments.AsNoTracking()
            .Where(p => p.Kind == CustomerPaymentKind.Payment && p.BankReference == reference
                && (p.Method == CustomerPaymentMethod.VietQrInvoice || p.StatusId != StatusIds.CustomerPayment.Cancelled))
            // The VietQR one first: it is what the service reported before.
            .OrderBy(p => p.Method == CustomerPaymentMethod.VietQrInvoice ? 0 : 1).ThenBy(p => p.Code)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, decimal>> ReceivedWithoutInvoiceAsync(IReadOnlyCollection<string> invoiceCodes, CancellationToken cancellationToken)
    {
        var codes = invoiceCodes.Distinct().ToList();
        if (codes.Count == 0)
        {
            return new Dictionary<string, decimal>();
        }

        return await db.CustomerPayments.AsNoTracking()
            .Where(p => p.TransferInvoiceCode != null && codes.Contains(p.TransferInvoiceCode) && p.InvoiceId == null && p.StatusId != StatusIds.CustomerPayment.Cancelled)
            .GroupBy(p => p.TransferInvoiceCode!)
            .Select(g => new { Code = g.Key, Amount = g.Sum(p => p.Amount) })
            .ToDictionaryAsync(g => g.Code, g => g.Amount, StringComparer.OrdinalIgnoreCase, cancellationToken);
    }

    public async Task<(IReadOnlyList<CustomerPayment> Items, int Total, decimal TotalAmount)> AwaitingConfirmationAsync(
        int page, int pageSize, CancellationToken cancellationToken)
    {
        var awaiting = db.CustomerPayments.AsNoTracking().Where(p => p.StatusId == StatusIds.CustomerPayment.AwaitingConfirmation);
        var total = await awaiting.CountAsync(cancellationToken);
        var amount = total == 0 ? 0m : await awaiting.SumAsync(p => p.Amount, cancellationToken);
        var items = await With(awaiting)
            .OrderBy(p => p.PaymentDate).ThenBy(p => p.Code)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(cancellationToken);
        return (items, total, amount);
    }
}
