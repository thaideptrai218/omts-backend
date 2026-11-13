using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Purchasing;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Persistence.Queries;

namespace OMTS.Infrastructure.Persistence.Repositories;

internal sealed class SupplierRepository(OmtsDbContext db) : ISupplierRepository
{
    public Task<Supplier?> GetForEditAsync(Guid supplierId, CancellationToken cancellationToken) =>
        db.Suppliers.FirstOrDefaultAsync(s => s.Id == supplierId, cancellationToken);

    public Task<Supplier?> FindByPhoneAsync(string phoneNumber, Guid? exceptSupplierId, CancellationToken cancellationToken) =>
        db.Suppliers.AsNoTracking().FirstOrDefaultAsync(s => s.PhoneNumber == phoneNumber && s.Id != exceptSupplierId, cancellationToken);

    public Task<Supplier?> FindByCodeAsync(string code, Guid? exceptSupplierId, CancellationToken cancellationToken)
    {
        var upper = code.ToUpperInvariant();
        return db.Suppliers.AsNoTracking().FirstOrDefaultAsync(s => s.Code.ToUpper() == upper && s.Id != exceptSupplierId, cancellationToken);
    }

    public async Task<IReadOnlySet<string>> ExistingPhonesAsync(IReadOnlyCollection<string> phoneNumbers, CancellationToken cancellationToken) =>
        (await db.Suppliers.AsNoTracking().Where(s => phoneNumbers.Contains(s.PhoneNumber)).Select(s => s.PhoneNumber).ToListAsync(cancellationToken)).ToHashSet();

    public async Task<(IReadOnlyList<SupplierFigureRow> Items, int Total)> SearchAsync(SupplierSearchFilter filter, int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = Filtered(filter);
        var total = await query.CountAsync(cancellationToken);
        var rows = await Sorted(query, filter).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        return (rows.Select(Row).ToList(), total);
    }

    public async Task<IReadOnlyList<SupplierFigureRow>> ListAsync(SupplierSearchFilter filter, CancellationToken cancellationToken) =>
        (await Sorted(Filtered(filter), filter).ToListAsync(cancellationToken)).Select(Row).ToList();

    public async Task<(IReadOnlyList<SupplierHistoryRow> Items, int Total)> HistoryAsync(Guid supplierId, int page, int pageSize, CancellationToken cancellationToken)
    {
        var receipts = db.PurchaseReceipts.AsNoTracking().Where(r => r.SupplierId == supplierId);
        var returns = db.PurchaseReturns.AsNoTracking().Where(r => r.SupplierId == supplierId && r.StatusId != StatusIds.PurchaseReturn.Draft);
        var total = await receipts.CountAsync(cancellationToken) + await returns.CountAsync(cancellationToken);
        // Anonymous shapes: EF translates a set operation over them, not over a constructor call.
        var rows = await receipts
            .Select(r => new { r.Id, ReceiptId = (Guid?)r.Id, IsReturn = false, r.Code, Date = r.ReceiptDate, r.CreatedAt, r.RecordedByAccountId, Total = (r.Lines.Sum(l => (decimal?)l.LineAmount) ?? 0m) - r.Discount, r.StatusId })
            .Concat(returns.Select(r => new { r.Id, r.ReceiptId, IsReturn = true, r.Code, Date = r.ReturnDate, r.CreatedAt, r.RecordedByAccountId, Total = (r.Lines.Sum(l => (decimal?)l.ReturnValue) ?? 0m) - r.Discount, r.StatusId }))
            .OrderByDescending(r => r.Date).ThenByDescending(r => r.CreatedAt).ThenByDescending(r => r.Code)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(cancellationToken);
        var items = rows.Select(r => new SupplierHistoryRow(r.Id, r.ReceiptId, r.IsReturn, r.Code, r.Date, r.CreatedAt, r.RecordedByAccountId, r.Total, r.StatusId)).ToList();
        return (items, total);
    }

    public void Add(Supplier supplier) => db.Suppliers.Add(supplier);

    private IQueryable<SupplierFigures> Filtered(SupplierSearchFilter filter)
    {
        var suppliers = db.Suppliers.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var text = filter.Search.Trim();
            suppliers = suppliers.Where(s => s.Name.Contains(text) || s.PhoneNumber.Contains(text) || s.Code.Contains(text));
        }

        if (filter.Type is { } type)
        {
            suppliers = suppliers.Where(s => s.SupplierType == type);
        }

        if (filter.StatusId is { } statusId)
        {
            suppliers = suppliers.Where(s => s.StatusId == statusId);
        }

        var query = SupplierDebtQuery.Figures(db, suppliers, filter.Today, filter.PurchaseFrom, filter.PurchaseTo);
        if (filter.PurchaseFrom is not null || filter.PurchaseTo is not null)
        {
            query = query.Where(f => f.PurchasedInPeriod);
        }

        if (filter.TotalPurchasedFrom is { } totalFrom)
        {
            query = query.Where(f => f.Purchased >= totalFrom);
        }

        if (filter.TotalPurchasedTo is { } totalTo)
        {
            query = query.Where(f => f.Purchased <= totalTo);
        }

        if (filter.CurrentDebtFrom is { } debtFrom)
        {
            query = query.Where(f => f.Debt >= debtFrom);
        }

        if (filter.CurrentDebtTo is { } debtTo)
        {
            query = query.Where(f => f.Debt <= debtTo);
        }

        return query;
    }

    private static IQueryable<SupplierFigures> Sorted(IQueryable<SupplierFigures> query, SupplierSearchFilter filter)
    {
        var descending = filter.Descending;
        var sorted = filter.Sort switch
        {
            SupplierSort.Code => descending ? query.OrderByDescending(f => f.Supplier.Code) : query.OrderBy(f => f.Supplier.Code),
            SupplierSort.Phone => descending ? query.OrderByDescending(f => f.Supplier.PhoneNumber) : query.OrderBy(f => f.Supplier.PhoneNumber),
            SupplierSort.Type => descending ? query.OrderByDescending(f => f.Supplier.SupplierType) : query.OrderBy(f => f.Supplier.SupplierType),
            SupplierSort.Province => descending ? query.OrderByDescending(f => f.Supplier.Province) : query.OrderBy(f => f.Supplier.Province),
            SupplierSort.CreatedAt => descending ? query.OrderByDescending(f => f.Supplier.CreatedAt) : query.OrderBy(f => f.Supplier.CreatedAt),
            SupplierSort.Status => descending ? query.OrderByDescending(f => f.Supplier.StatusId) : query.OrderBy(f => f.Supplier.StatusId),
            SupplierSort.TotalPurchased => descending ? query.OrderByDescending(f => f.Purchased) : query.OrderBy(f => f.Purchased),
            SupplierSort.CurrentDebt => descending ? query.OrderByDescending(f => f.Debt) : query.OrderBy(f => f.Debt),
            _ => descending ? query.OrderByDescending(f => f.Supplier.Name) : query.OrderBy(f => f.Supplier.Name),
        };
        return sorted.ThenBy(f => f.Supplier.Name).ThenBy(f => f.Supplier.Id);
    }

    private static SupplierFigureRow Row(SupplierFigures f) => new(f.Supplier, f.Purchased, f.Debt);
}

internal sealed class FarmingInputItemRepository(OmtsDbContext db) : IFarmingInputItemRepository
{
    public Task<FarmingInputItem?> GetForEditAsync(Guid itemId, CancellationToken cancellationToken) =>
        db.FarmingInputItems
            .Include(i => i.Units).ThenInclude(u => u.Unit)
            .Include(i => i.Photos)
            .Include(i => i.ItemGroup)
            .Include(i => i.Product)
            .AsSplitQuery()
            .FirstOrDefaultAsync(i => i.Id == itemId, cancellationToken);

    public async Task<IReadOnlyList<FarmingInputItem>> GetByIdsAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken) =>
        await db.FarmingInputItems.AsNoTracking().Include(i => i.Units).ThenInclude(u => u.Unit).Where(i => itemIds.Contains(i.Id)).AsSplitQuery().ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<FarmingInputItem>> GetByIdsForUpdateAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken) =>
        await db.FarmingInputItems.Include(i => i.Units).ThenInclude(u => u.Unit).Where(i => itemIds.Contains(i.Id)).AsSplitQuery().ToListAsync(cancellationToken);

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

    public async Task<CodeOwner?> FindCodeOwnerAsync(string code, Guid? exceptItemId, Guid? exceptItemUnitId, CancellationToken cancellationToken)
    {
        var upper = code.ToUpperInvariant();
        if (await db.FarmingInputItems.AsNoTracking().Where(i => i.Code.ToUpper() == upper && i.Id != exceptItemId).Select(i => (Guid?)i.Id).FirstOrDefaultAsync(cancellationToken) is { } itemId)
        {
            return new CodeOwner(itemId, false);
        }

        var unitOwner = await db.InputItemUnits.AsNoTracking().Where(u => u.Code.ToUpper() == upper && u.Id != exceptItemUnitId).Select(u => (Guid?)u.ItemId).FirstOrDefaultAsync(cancellationToken);
        return unitOwner is { } owner ? new CodeOwner(owner, true) : null;
    }

    public async Task<IReadOnlyList<FarmingInputItem>> ListAsync(string? search, InputItemGroup? group, int? statusId, CancellationToken cancellationToken)
    {
        var query = db.FarmingInputItems.AsNoTracking().Where(i => !i.IsHidden);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var text = search.Trim();
            query = query.Where(i => i.Code.Contains(text) || i.Name.Contains(text) || i.Units.Any(u => u.Code.Contains(text)));
        }

        if (group is not null)
        {
            query = query.Where(i => i.Group == group);
        }

        if (statusId is not null)
        {
            query = query.Where(i => i.StatusId == statusId);
        }

        return await query.Include(i => i.Units).ThenInclude(u => u.Unit).Include(i => i.ItemGroup).Include(i => i.Product).AsSplitQuery().OrderBy(i => i.Code).ToListAsync(cancellationToken);
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

    public async Task<IReadOnlySet<Guid>> UsedUnitsAmongAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken)
    {
        if (itemIds.Count == 0)
        {
            return new HashSet<Guid>();
        }

        var onReceipts = db.PurchaseReceiptLines.Where(l => l.ItemUnitId != null && l.ItemId != null && itemIds.Contains(l.ItemId.Value)).Select(l => l.ItemUnitId!.Value);
        var onReturns = db.PurchaseReturnLines.Where(l => l.ItemUnitId != null && itemIds.Contains(l.ItemId)).Select(l => l.ItemUnitId!.Value);
        return (await onReceipts.Union(onReturns).ToListAsync(cancellationToken)).ToHashSet();
    }

    public async Task<IReadOnlyDictionary<Guid, decimal>> LatestPurchasePricesAsync(
        IReadOnlyCollection<Guid> itemIds, Guid? exceptReceiptId, CancellationToken cancellationToken)
    {
        if (itemIds.Count == 0)
        {
            return new Dictionary<Guid, decimal>();
        }

        var rows = await db.PurchaseReceiptLines.AsNoTracking()
            .Where(l => l.ItemId != null && itemIds.Contains(l.ItemId.Value) && l.EnteredUnitPrice != null && l.Receipt.StatusId == StatusIds.PurchaseReceipt.Completed && l.ReceiptId != exceptReceiptId)
            .Select(l => new { ItemId = l.ItemId!.Value, l.Receipt.ReceivedAt, l.LineNumber, Price = l.EnteredUnitPrice!.Value, l.EnteredDiscount, l.UnitFactor })
            .ToListAsync(cancellationToken);
        return rows.GroupBy(r => r.ItemId).ToDictionary(
            g => g.Key,
            g => g.OrderByDescending(r => r.ReceivedAt).ThenByDescending(r => r.LineNumber).Select(r => decimal.Round((r.Price - r.EnteredDiscount) / r.UnitFactor, 4)).First());
    }

    public async Task<bool> HasMovementsAsync(Guid itemId, CancellationToken cancellationToken) =>
        await db.InputStockMovements.AnyAsync(m => m.ItemId == itemId, cancellationToken)
        || await db.PurchaseReceiptLines.AnyAsync(l => l.ItemId == itemId, cancellationToken)
        || await db.PurchaseReturnLines.AnyAsync(l => l.ItemId == itemId, cancellationToken)
        || await db.MaterialUsages.AnyAsync(u => u.ItemId == itemId, cancellationToken);

    public void Add(FarmingInputItem item) => db.FarmingInputItems.Add(item);

    public void AddUnit(InputItemUnit unit) => db.InputItemUnits.Add(unit);

    public void RemoveUnit(InputItemUnit unit) => db.InputItemUnits.Remove(unit);

    public void AddPhoto(InputItemPhoto photo) => db.InputItemPhotos.Add(photo);

    public void RemovePhoto(InputItemPhoto photo) => db.InputItemPhotos.Remove(photo);
}
internal sealed partial class PurchaseReceiptRepository(OmtsDbContext db) : IPurchaseReceiptRepository
{
    public Task<PurchaseReceipt?> GetForEditAsync(Guid receiptId, CancellationToken cancellationToken) =>
        db.PurchaseReceipts
            .Include(r => r.Supplier)
            .Include(r => r.Zone)
            .Include(r => r.Lines).ThenInclude(l => l.Item)
            .Include(r => r.Lines).ThenInclude(l => l.Raft!).ThenInclude(r => r.Zone)
            .Include(r => r.Returns).ThenInclude(rt => rt.Lines)
            .AsSplitQuery()
            .FirstOrDefaultAsync(r => r.Id == receiptId, cancellationToken);

    public void Add(PurchaseReceipt receipt) => db.PurchaseReceipts.Add(receipt);

    public void Remove(PurchaseReceipt receipt) => db.PurchaseReceipts.Remove(receipt);

    public void AddReturn(PurchaseReturn purchaseReturn) => db.PurchaseReturns.Add(purchaseReturn);

    public void RemoveLine(PurchaseReceiptLine line) => db.PurchaseReceiptLines.Remove(line);

    public void AddLine(PurchaseReceiptLine line) => db.PurchaseReceiptLines.Add(line);

    public Task<PurchaseReturn?> GetReturnAsync(Guid returnId, CancellationToken cancellationToken) =>
        db.PurchaseReturns.AsNoTracking()
            .Include(r => r.Receipt)
            .Include(r => r.Supplier)
            .Include(r => r.Zone)
            .Include(r => r.Lines).ThenInclude(l => l.Item)
            .AsSplitQuery()
            .FirstOrDefaultAsync(r => r.Id == returnId, cancellationToken);

    public Task<PurchaseReturn?> GetReturnForEditAsync(Guid returnId, CancellationToken cancellationToken) =>
        db.PurchaseReturns.Include(r => r.Lines).FirstOrDefaultAsync(r => r.Id == returnId, cancellationToken);

    public async Task<IReadOnlyList<SupplierPayment>> RefundsOfReturnAsync(Guid returnId, CancellationToken cancellationToken) =>
        await db.SupplierPayments.AsNoTracking().Where(p => p.PurchaseReturnId == returnId)
            .OrderBy(p => p.PaymentDate).ThenBy(p => p.Code).ToListAsync(cancellationToken);

    public void RemoveReturn(PurchaseReturn purchaseReturn) => db.PurchaseReturns.Remove(purchaseReturn);

    public void AddReturnLine(PurchaseReturnLine line) => db.PurchaseReturnLines.Add(line);

    public void RemoveReturnLine(PurchaseReturnLine line) => db.PurchaseReturnLines.Remove(line);
}

internal sealed class SupplierPaymentRepository(OmtsDbContext db) : ISupplierPaymentRepository
{
    public Task<SupplierPayment?> GetForEditAsync(Guid paymentId, CancellationToken cancellationToken) =>
        db.SupplierPayments.Include(p => p.Supplier).Include(p => p.CashAccount).Include(p => p.Allocations).FirstOrDefaultAsync(p => p.Id == paymentId, cancellationToken);

    public async Task<IReadOnlyList<PaymentAllocationRow>> AllocationsOfAsync(Guid paymentId, CancellationToken cancellationToken) =>
        await db.SupplierPaymentAllocations.AsNoTracking()
            .Where(a => a.PaymentId == paymentId)
            .OrderBy(a => a.Receipt.ReceivedAt).ThenBy(a => a.Receipt.Code)
            .Select(a => new PaymentAllocationRow(
                a.ReceiptId, a.Receipt.Code, a.Receipt.ReceivedAt, (a.Receipt.Lines.Sum(l => (decimal?)l.LineAmount) ?? 0m) - a.Receipt.Discount, a.Amount))
            .ToListAsync(cancellationToken);

    public Task<SupplierPayment?> FindByDeduplicationKeyAsync(string key, CancellationToken cancellationToken) =>
        db.SupplierPayments.AsNoTracking().Include(p => p.Supplier).Include(p => p.CashAccount).FirstOrDefaultAsync(p => p.DeduplicationKey == key, cancellationToken);

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
        var items = await query.Include(p => p.Supplier).Include(p => p.CashAccount)
            .OrderByDescending(p => p.PaidAt).ThenByDescending(p => p.Code)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(cancellationToken);
        return (items, total, paid, paidBack);
    }

    public void Add(SupplierPayment payment) => db.SupplierPayments.Add(payment);
}

