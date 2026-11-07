using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Purchasing;
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

    public Task<FarmingInputItem?> FindByNameAsync(string name, Guid? exceptItemId, CancellationToken cancellationToken)
    {
        var upper = name.ToUpperInvariant();
        return db.FarmingInputItems.AsNoTracking().FirstOrDefaultAsync(i => i.Name.ToUpper() == upper && i.Id != exceptItemId, cancellationToken);
    }

    public Task<string?> SeedUnitAsync(Guid? exceptItemId, CancellationToken cancellationToken) =>
        db.FarmingInputItems.AsNoTracking()
            .Where(i => i.Group == InputItemGroup.Seed && i.Id != exceptItemId)
            .OrderBy(i => i.CreatedAt).ThenBy(i => i.Id)
            .Select(i => (string?)i.Unit)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<string>> CodesStartingWithAsync(string prefix, CancellationToken cancellationToken)
    {
        var upper = prefix.ToUpperInvariant();
        return await db.FarmingInputItems.AsNoTracking().Where(i => i.Code.ToUpper().StartsWith(upper)).Select(i => i.Code).ToListAsync(cancellationToken);
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
        || await db.MaterialUsages.AnyAsync(u => u.ItemId == itemId, cancellationToken);

    public async Task<IReadOnlySet<Guid>> UsedAmongAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken)
    {
        if (itemIds.Count == 0)
        {
            return new HashSet<Guid>();
        }

        var onReceipts = db.PurchaseReceiptLines.Where(l => l.ItemId != null && itemIds.Contains(l.ItemId.Value)).Select(l => l.ItemId!.Value);
        var onUsages = db.MaterialUsages.Where(u => itemIds.Contains(u.ItemId)).Select(u => u.ItemId);
        return (await onReceipts.Union(onUsages).ToListAsync(cancellationToken)).ToHashSet();
    }

    public void Add(FarmingInputItem item) => db.FarmingInputItems.Add(item);
}

internal sealed partial class PurchaseReceiptRepository(OmtsDbContext db) : IPurchaseReceiptRepository
{
    public Task<PurchaseReceipt?> GetForEditAsync(Guid receiptId, CancellationToken cancellationToken) =>
        db.PurchaseReceipts
            .Include(r => r.Supplier)
            .Include(r => r.Lines).ThenInclude(l => l.Item)
            .Include(r => r.Lines).ThenInclude(l => l.Raft!).ThenInclude(r => r.Zone)
            .Include(r => r.Returns).ThenInclude(rt => rt.Lines)
            .AsSplitQuery()
            .FirstOrDefaultAsync(r => r.Id == receiptId, cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, (decimal Seeded, bool UnknownQuantity)>> SeededAsync(
        IReadOnlyCollection<Guid> lineIds, CancellationToken cancellationToken)
    {
        var rows = await db.SeedingEvents.AsNoTracking()
            .Where(e => lineIds.Contains(e.SeedLotId))
            .Select(e => new { e.SeedLotId, e.SeedQuantity })
            .ToListAsync(cancellationToken);
        return rows.GroupBy(r => r.SeedLotId)
            .ToDictionary(g => g.Key, g => (g.Sum(r => r.SeedQuantity ?? 0), g.Any(r => r.SeedQuantity is null)));
    }

    public void Add(PurchaseReceipt receipt) => db.PurchaseReceipts.Add(receipt);

    public void Remove(PurchaseReceipt receipt) => db.PurchaseReceipts.Remove(receipt);

    public void AddReturn(PurchaseReturn purchaseReturn) => db.PurchaseReturns.Add(purchaseReturn);

    public void RemoveLine(PurchaseReceiptLine line) => db.PurchaseReceiptLines.Remove(line);

    public void AddLine(PurchaseReceiptLine line) => db.PurchaseReceiptLines.Add(line);

    public Task<PurchaseReturn?> GetReturnAsync(Guid returnId, CancellationToken cancellationToken) =>
        db.PurchaseReturns.AsNoTracking()
            .Include(r => r.Receipt)
            .Include(r => r.Lines).ThenInclude(l => l.ReceiptLine).ThenInclude(l => l.Item)
            .FirstOrDefaultAsync(r => r.Id == returnId, cancellationToken);
}

internal sealed class SupplierPaymentRepository(OmtsDbContext db) : ISupplierPaymentRepository
{
    public Task<SupplierPayment?> GetForEditAsync(Guid paymentId, CancellationToken cancellationToken) =>
        db.SupplierPayments.Include(p => p.Supplier).FirstOrDefaultAsync(p => p.Id == paymentId, cancellationToken);

    public Task<SupplierPayment?> FindByDeduplicationKeyAsync(string key, CancellationToken cancellationToken) =>
        db.SupplierPayments.AsNoTracking().Include(p => p.Supplier).FirstOrDefaultAsync(p => p.DeduplicationKey == key, cancellationToken);

    public Task<SupplierPayment?> FindRecordedByBankReferenceAsync(string bankReference, CancellationToken cancellationToken) =>
        db.SupplierPayments.AsNoTracking().Include(p => p.Supplier)
            .Where(p => p.BankReference == bankReference && p.StatusId == StatusIds.SupplierPayment.Recorded)
            .OrderByDescending(p => p.PaymentDate).ThenByDescending(p => p.Code)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<(IReadOnlyList<SupplierPayment> Items, int Total, decimal TotalPaid, decimal TotalPaidBack)> SearchAsync(
        Guid? supplierId, SupplierPaymentKind? kind, PaymentMethod? method, int? statusId, DateOnly? from, DateOnly? to,
        int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = db.SupplierPayments.AsNoTracking()
            .Where(p => (supplierId == null || p.SupplierId == supplierId)
                && (kind == null || p.Kind == kind)
                && (method == null || p.Method == method)
                && (statusId == null || p.StatusId == statusId)
                && (from == null || p.PaymentDate >= from)
                && (to == null || p.PaymentDate <= to));
        var total = await query.CountAsync(cancellationToken);
        var recorded = query.Where(p => p.StatusId == StatusIds.SupplierPayment.Recorded);
        var paid = await recorded.Where(p => p.Kind == SupplierPaymentKind.Payment).SumAsync(p => (decimal?)p.Amount, cancellationToken) ?? 0;
        var paidBack = await recorded.Where(p => p.Kind == SupplierPaymentKind.PaidBack).SumAsync(p => (decimal?)p.Amount, cancellationToken) ?? 0;
        var items = await query.Include(p => p.Supplier)
            .OrderByDescending(p => p.PaymentDate).ThenByDescending(p => p.Code)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(cancellationToken);
        return (items, total, paid, paidBack);
    }

    public void Add(SupplierPayment payment) => db.SupplierPayments.Add(payment);
}
