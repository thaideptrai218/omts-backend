using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Enums;

namespace OMTS.Infrastructure.Persistence.Repositories;

internal sealed class InputStockRepository(OmtsDbContext db) : IInputStockRepository
{
    public async Task<IReadOnlyDictionary<Guid, decimal>> OnHandAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken)
    {
        if (itemIds.Count == 0)
        {
            return new Dictionary<Guid, decimal>();
        }

        var sums = await db.InputStockMovements.AsNoTracking().Where(m => itemIds.Contains(m.ItemId))
            .GroupBy(m => m.ItemId).Select(g => new { g.Key, Sum = g.Sum(m => m.Quantity) }).ToListAsync(cancellationToken);
        return itemIds.ToDictionary(id => id, id => sums.FirstOrDefault(s => s.Key == id)?.Sum ?? 0m);
    }

    public async Task<IReadOnlyList<InputStockMovement>> UnreversedOfDocumentAsync(Guid documentId, CancellationToken cancellationToken) =>
        await db.InputStockMovements.AsNoTracking()
            .Where(m => m.DocumentId == documentId && m.Kind != InputStockMovementKind.Reversal
                && !db.InputStockMovements.Any(r => r.ReversesMovementId == m.Id))
            .OrderBy(m => m.Sequence).ToListAsync(cancellationToken);

    public async Task<bool> HasOnlyOpeningAsync(Guid itemId, CancellationToken cancellationToken) =>
        !await db.InputStockMovements.AnyAsync(
            m => m.ItemId == itemId && m.Kind != InputStockMovementKind.Opening && m.Kind != InputStockMovementKind.Reversal, cancellationToken)
        && !await db.InputStockMovements.AnyAsync(
            m => m.ItemId == itemId && m.Kind == InputStockMovementKind.Reversal && m.DocumentId != itemId, cancellationToken);

    public async Task<decimal?> OpeningQuantityAsync(Guid itemId, CancellationToken cancellationToken) =>
        await db.InputStockMovements.AsNoTracking()
            .Where(m => m.ItemId == itemId && m.Kind == InputStockMovementKind.Opening && !db.InputStockMovements.Any(r => r.ReversesMovementId == m.Id))
            .Select(m => (decimal?)m.Quantity).FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyDictionary<(Guid ItemId, Guid? LotId), decimal>> BucketBalancesAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken)
    {
        if (itemIds.Count == 0)
        {
            return new Dictionary<(Guid, Guid?), decimal>();
        }

        var sums = await db.InputStockMovements.AsNoTracking().Where(m => itemIds.Contains(m.ItemId))
            .GroupBy(m => new { m.ItemId, m.SeedLotLineId }).Select(g => new { g.Key.ItemId, g.Key.SeedLotLineId, Sum = g.Sum(m => m.Quantity) }).ToListAsync(cancellationToken);
        return sums.ToDictionary(s => (s.ItemId, s.SeedLotLineId), s => s.Sum);
    }

    public async Task<IReadOnlyDictionary<Guid, string>> LotCodesAsync(IReadOnlyCollection<Guid> lotLineIds, CancellationToken cancellationToken)
    {
        if (lotLineIds.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        var rows = await db.PurchaseReceiptLines.AsNoTracking().Where(l => lotLineIds.Contains(l.Id)).Select(l => new { l.Id, ReceiptCode = l.Receipt.Code, l.LineNumber }).ToListAsync(cancellationToken);
        return rows.ToDictionary(r => r.Id, r => $"{r.ReceiptCode}-{r.LineNumber}");
    }

    public void Add(InputStockMovement movement) => db.InputStockMovements.Add(movement);
}

internal sealed class InputUnitRepository(OmtsDbContext db) : IInputUnitRepository
{
    public async Task<IReadOnlyList<InputUnit>> ListAsync(CancellationToken cancellationToken) =>
        await db.InputUnits.AsNoTracking().OrderBy(u => u.Name).ToListAsync(cancellationToken);

    public Task<InputUnit?> GetForEditAsync(Guid unitId, CancellationToken cancellationToken) =>
        db.InputUnits.FirstOrDefaultAsync(u => u.Id == unitId, cancellationToken);

    public async Task<IReadOnlyList<InputUnit>> GetByIdsAsync(IReadOnlyCollection<Guid> unitIds, CancellationToken cancellationToken) =>
        await db.InputUnits.Where(u => unitIds.Contains(u.Id)).ToListAsync(cancellationToken);

    public Task<InputUnit?> FindByNameAsync(string name, Guid? exceptUnitId, CancellationToken cancellationToken)
    {
        var upper = name.ToUpperInvariant();
        return db.InputUnits.FirstOrDefaultAsync(u => u.Name.ToUpper() == upper && u.Id != exceptUnitId, cancellationToken);
    }

    public async Task<IReadOnlyDictionary<Guid, InputUnitUsage>> UsageAsync(CancellationToken cancellationToken)
    {
        // The unit list is shared by the input items and the products: a unit either kind counts in cannot be deleted.
        var itemCounts = await db.InputItemUnits.AsNoTracking().GroupBy(u => u.UnitId).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(cancellationToken);
        var productCounts = await db.ProductUnits.AsNoTracking().GroupBy(u => u.UnitId).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(cancellationToken);
        var counts = itemCounts.Concat(productCounts).GroupBy(c => c.Key).Select(g => new { Key = g.Key, Count = g.Sum(c => c.Count) }).ToList();
        var onReceipts = db.PurchaseReceiptLines.Where(l => l.ItemUnitId != null).Select(l => l.ItemUnit!.UnitId);
        var onReturns = db.PurchaseReturnLines.Where(l => l.ItemUnitId != null).Select(l => l.ItemUnit!.UnitId);
        var onLines = (await onReceipts.Union(onReturns).ToListAsync(cancellationToken)).ToHashSet();
        return counts.ToDictionary(c => c.Key, c => new InputUnitUsage(c.Count, onLines.Contains(c.Key)));
    }

    public async Task<IReadOnlyList<FarmingInputItem>> ItemsWithBaseUnitAsync(Guid unitId, CancellationToken cancellationToken) =>
        await db.FarmingInputItems.Where(i => i.Units.Any(u => u.UnitId == unitId && u.IsBase)).ToListAsync(cancellationToken);

    public void Add(InputUnit unit) => db.InputUnits.Add(unit);

    public void Remove(InputUnit unit) => db.InputUnits.Remove(unit);
}

internal sealed class InputItemCategoryRepository(OmtsDbContext db) : IInputItemCategoryRepository
{
    public async Task<IReadOnlyList<InputItemCategory>> ListAsync(CancellationToken cancellationToken) =>
        await db.InputItemGroups.AsNoTracking().OrderBy(g => g.SortOrder).ThenBy(g => g.Name).ToListAsync(cancellationToken);

    public Task<InputItemCategory?> GetForEditAsync(Guid groupId, CancellationToken cancellationToken) =>
        db.InputItemGroups.FirstOrDefaultAsync(g => g.Id == groupId, cancellationToken);

    public async Task<IReadOnlyList<InputItemCategory>> GetAllForEditAsync(CancellationToken cancellationToken) =>
        await db.InputItemGroups.OrderBy(g => g.SortOrder).ThenBy(g => g.Name).ToListAsync(cancellationToken);

    public Task<InputItemCategory?> FindByNameAsync(string name, Guid? exceptGroupId, CancellationToken cancellationToken)
    {
        var upper = name.ToUpperInvariant();
        return db.InputItemGroups.AsNoTracking().FirstOrDefaultAsync(g => g.Name.ToUpper() == upper && g.Id != exceptGroupId, cancellationToken);
    }

    public async Task<IReadOnlyDictionary<Guid, int>> ItemCountsAsync(CancellationToken cancellationToken) =>
        await db.FarmingInputItems.AsNoTracking().Where(i => i.ItemGroupId != null && !i.IsHidden)
            .GroupBy(i => i.ItemGroupId!.Value).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, cancellationToken);

    public async Task<IReadOnlyList<FarmingInputItem>> HiddenItemsAsync(Guid groupId, CancellationToken cancellationToken) =>
        await db.FarmingInputItems.Where(i => i.ItemGroupId == groupId && i.IsHidden).ToListAsync(cancellationToken);

    public void Add(InputItemCategory group) => db.InputItemGroups.Add(group);

    public void Remove(InputItemCategory group) => db.InputItemGroups.Remove(group);
}

