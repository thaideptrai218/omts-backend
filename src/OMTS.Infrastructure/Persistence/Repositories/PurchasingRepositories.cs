using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Enums;

namespace OMTS.Infrastructure.Persistence.Repositories;

internal sealed class SupplierRepository(OmtsDbContext db) : ISupplierRepository
{
    public Task<Supplier?> GetForEditAsync(Guid supplierId, CancellationToken cancellationToken) =>
        db.Suppliers.FirstOrDefaultAsync(s => s.Id == supplierId, cancellationToken);

    public Task<Supplier?> FindByPhoneAsync(string phoneNumber, Guid? exceptSupplierId, CancellationToken cancellationToken) =>
        db.Suppliers.AsNoTracking().FirstOrDefaultAsync(s => s.PhoneNumber == phoneNumber && s.Id != exceptSupplierId, cancellationToken);

    public async Task<(IReadOnlyList<Supplier> Items, int Total)> SearchAsync(
        string? search, SupplierType? type, int? statusId, int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = db.Suppliers.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var text = search.Trim();
            query = query.Where(s => s.Name.Contains(text) || s.PhoneNumber.Contains(text));
        }

        if (type is not null)
        {
            query = query.Where(s => s.SupplierType == type);
        }

        if (statusId is not null)
        {
            query = query.Where(s => s.StatusId == statusId);
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderBy(s => s.Name).ThenBy(s => s.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        return (items, total);
    }

    public void Add(Supplier supplier) => db.Suppliers.Add(supplier);
}

internal sealed class FarmingInputItemRepository(OmtsDbContext db) : IFarmingInputItemRepository
{
    public Task<FarmingInputItem?> GetForEditAsync(Guid itemId, CancellationToken cancellationToken) =>
        db.FarmingInputItems.FirstOrDefaultAsync(i => i.Id == itemId, cancellationToken);

    public async Task<IReadOnlyList<FarmingInputItem>> GetByIdsAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken) =>
        await db.FarmingInputItems.AsNoTracking().Where(i => itemIds.Contains(i.Id)).ToListAsync(cancellationToken);

    public Task<FarmingInputItem?> FindByCodeAsync(string code, Guid? exceptItemId, CancellationToken cancellationToken)
    {
        var upper = code.ToUpperInvariant();
        return db.FarmingInputItems.AsNoTracking().FirstOrDefaultAsync(i => i.Code.ToUpper() == upper && i.Id != exceptItemId, cancellationToken);
    }

    public async Task<IReadOnlyList<FarmingInputItem>> ListAsync(string? search, InputItemGroup? group, int? statusId, CancellationToken cancellationToken)
    {
        var query = db.FarmingInputItems.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var text = search.Trim();
            query = query.Where(i => i.Code.Contains(text) || i.Name.Contains(text));
        }

        if (group is not null)
        {
            query = query.Where(i => i.Group == group);
        }

        if (statusId is not null)
        {
            query = query.Where(i => i.StatusId == statusId);
        }

        return await query.OrderBy(i => i.Code).ToListAsync(cancellationToken);
    }

    public async Task<bool> IsUsedAsync(Guid itemId, CancellationToken cancellationToken) =>
        await db.PurchaseReceiptLines.AnyAsync(l => l.ItemId == itemId, cancellationToken)
        || await db.CultivationCycles.AnyAsync(c => c.PlannedSeedTypeId == itemId, cancellationToken);

    public async Task<IReadOnlySet<Guid>> UsedAmongAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken)
    {
        if (itemIds.Count == 0)
        {
            return new HashSet<Guid>();
        }

        var onReceipts = await db.PurchaseReceiptLines.Where(l => itemIds.Contains(l.ItemId)).Select(l => l.ItemId).Distinct().ToListAsync(cancellationToken);
        var onCycles = await db.CultivationCycles.Where(c => c.PlannedSeedTypeId != null && itemIds.Contains(c.PlannedSeedTypeId.Value))
            .Select(c => c.PlannedSeedTypeId!.Value).Distinct().ToListAsync(cancellationToken);
        return onReceipts.Concat(onCycles).ToHashSet();
    }

    public void Add(FarmingInputItem item) => db.FarmingInputItems.Add(item);
}

internal sealed partial class PurchaseReceiptRepository(OmtsDbContext db) : IPurchaseReceiptRepository
{
    public Task<PurchaseReceipt?> GetForEditAsync(Guid receiptId, CancellationToken cancellationToken) =>
        db.PurchaseReceipts
            .Include(r => r.Supplier)
            .Include(r => r.Zone)
            .Include(r => r.Raft)
            .Include(r => r.Lines).ThenInclude(l => l.Item)
            .Include(r => r.Lines).ThenInclude(l => l.SeedLot)
            .Include(r => r.Returns).ThenInclude(rt => rt.Lines)
            .Include(r => r.Payments)
            .AsSplitQuery()
            .FirstOrDefaultAsync(r => r.Id == receiptId, cancellationToken);

    public Task<int> CountCodesAsync(string prefix, CancellationToken cancellationToken) =>
        db.PurchaseReceipts.CountAsync(r => r.Code.StartsWith(prefix), cancellationToken);

    public Task<int> CountReturnCodesAsync(string prefix, CancellationToken cancellationToken) =>
        db.PurchaseReturns.CountAsync(r => r.Code.StartsWith(prefix), cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, (decimal Stocked, bool UnknownQuantity)>> StockedAsync(
        IReadOnlyCollection<Guid> lineIds, CancellationToken cancellationToken)
    {
        var rows = await db.SeedingEvents.AsNoTracking()
            .Where(e => lineIds.Contains(e.SeedLot.PurchaseReceiptLineId))
            .Select(e => new { e.SeedLot.PurchaseReceiptLineId, e.SeedQuantity })
            .ToListAsync(cancellationToken);
        return rows.GroupBy(r => r.PurchaseReceiptLineId)
            .ToDictionary(g => g.Key, g => (g.Sum(r => r.SeedQuantity ?? 0), g.Any(r => r.SeedQuantity is null)));
    }

    public async Task<IReadOnlyList<PurchaseReceipt>> SearchAsync(PurchaseReceiptFilter filter, CancellationToken cancellationToken) =>
        await Filtered(filter, WithFigures(db.PurchaseReceipts.AsNoTracking()).Include(r => r.Supplier).Include(r => r.Zone).Include(r => r.Raft))
            .OrderByDescending(r => r.ReceiptDate).ThenByDescending(r => r.Code)
            .ToListAsync(cancellationToken);

    public void Add(PurchaseReceipt receipt) => db.PurchaseReceipts.Add(receipt);

    public void AddReturn(PurchaseReturn purchaseReturn) => db.PurchaseReturns.Add(purchaseReturn);

    public void RemoveLine(PurchaseReceiptLine line) => db.PurchaseReceiptLines.Remove(line);

    public void AddLine(PurchaseReceiptLine line) => db.PurchaseReceiptLines.Add(line);

    public void AddSeedLot(Domain.Entities.Cultivation.SeedReceiptLot lot) => db.SeedReceiptLots.Add(lot);

    public Task<PurchaseReturn?> GetReturnAsync(Guid returnId, CancellationToken cancellationToken) =>
        db.PurchaseReturns.AsNoTracking()
            .Include(r => r.Receipt)
            .Include(r => r.Lines).ThenInclude(l => l.ReceiptLine).ThenInclude(l => l.Item)
            .FirstOrDefaultAsync(r => r.Id == returnId, cancellationToken);

    // Lines, returns and payments are what the money figures are computed from.
    private static IQueryable<PurchaseReceipt> WithFigures(IQueryable<PurchaseReceipt> query) => query
        .Include(r => r.Lines).ThenInclude(l => l.Item)
        .Include(r => r.Returns).ThenInclude(rt => rt.Lines)
        .Include(r => r.Payments)
        .AsSplitQuery();
}

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
