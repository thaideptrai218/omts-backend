using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Enums;

namespace OMTS.Infrastructure.Persistence.Repositories.Purchasing;

internal sealed class SupplierPaymentRepository(OmtsDbContext db) : ISupplierPaymentRepository
{
    public Task<SupplierPayment?> GetForEditAsync(Guid paymentId, CancellationToken cancellationToken) =>
        db.SupplierPayments.Include(p => p.Supplier).FirstOrDefaultAsync(p => p.Id == paymentId, cancellationToken);

    public Task<SupplierPayment?> FindByDeduplicationKeyAsync(string key, CancellationToken cancellationToken) =>
        db.SupplierPayments.AsNoTracking().Include(p => p.Supplier).FirstOrDefaultAsync(p => p.DeduplicationKey == key, cancellationToken);

    public async Task<(IReadOnlyList<SupplierPayment> Items, int Total, decimal TotalAmount)> SearchAsync(
        Guid? supplierId, Guid? receiptId, Guid? zoneId, PaymentMethod? method, int? statusId, DateOnly? from, DateOnly? to,
        int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = db.SupplierPayments.AsNoTracking()
            .Where(p => (supplierId == null || p.SupplierId == supplierId)
                && (receiptId == null || p.ReceiptId == receiptId)
                && (zoneId == null || (p.Receipt != null && p.Receipt.ZoneId == zoneId))
                && (method == null || p.Method == method)
                && (statusId == null || p.StatusId == statusId)
                && (from == null || p.PaymentDate >= from)
                && (to == null || p.PaymentDate <= to));
        var total = await query.CountAsync(cancellationToken);
        var amount = await query.SumAsync(p => (decimal?)p.Amount, cancellationToken) ?? 0;
        var items = await query.Include(p => p.Supplier).Include(p => p.Receipt)
            .OrderByDescending(p => p.PaymentDate).ThenByDescending(p => p.CreatedAt)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(cancellationToken);
        return (items, total, amount);
    }

    public async Task<IReadOnlyDictionary<Guid, decimal>> UnallocatedAsync(IReadOnlyCollection<Guid>? supplierIds, CancellationToken cancellationToken) =>
        await db.SupplierPayments.AsNoTracking()
            .Where(p => p.ReceiptId == null && p.StatusId == StatusIds.SupplierPayment.Recorded && (supplierIds == null || supplierIds.Contains(p.SupplierId)))
            .GroupBy(p => p.SupplierId)
            .Select(g => new { g.Key, Amount = g.Sum(p => p.Amount) })
            .ToDictionaryAsync(x => x.Key, x => x.Amount, cancellationToken);

    public void Add(SupplierPayment payment) => db.SupplierPayments.Add(payment);
}
