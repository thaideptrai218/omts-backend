using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Enums;

namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>The movement ledger of the input items (append-only).</summary>
public interface IInputStockRepository
{
    /// <summary>The quantity on hand (Σ movements, base units) of each item; an item with no row is 0.</summary>
    Task<IReadOnlyDictionary<Guid, decimal>> OnHandAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken);

    /// <summary>The rows of a document (or of the opening of an item) that no reversal row undoes yet, in write order; tracked-free.</summary>
    Task<IReadOnlyList<InputStockMovement>> UnreversedOfDocumentAsync(Guid documentId, CancellationToken cancellationToken);

    /// <summary>True when every row of the item is an Opening row or undone by a reversal of one (an item with no row counts).</summary>
    Task<bool> HasOnlyOpeningAsync(Guid itemId, CancellationToken cancellationToken);

    /// <summary>The unreversed opening quantity of the item, null without one.</summary>
    Task<decimal?> OpeningQuantityAsync(Guid itemId, CancellationToken cancellationToken);

    /// <summary>The quantity of each item on the ledger per seed lot: the key holds the lot (a receipt line), or null for the quantity of the item that has no lot. Items with no row are absent.</summary>
    Task<IReadOnlyDictionary<(Guid ItemId, Guid? LotId), decimal>> BucketBalancesAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken);

    /// <summary>The codes of seed lots (PN000001-1) by receipt line id.</summary>
    Task<IReadOnlyDictionary<Guid, string>> LotCodesAsync(IReadOnlyCollection<Guid> lotLineIds, CancellationToken cancellationToken);

    void Add(InputStockMovement movement);
}

public interface IInputUnitRepository
{
    Task<IReadOnlyList<InputUnit>> ListAsync(CancellationToken cancellationToken);
    Task<InputUnit?> GetForEditAsync(Guid unitId, CancellationToken cancellationToken);
    Task<IReadOnlyList<InputUnit>> GetByIdsAsync(IReadOnlyCollection<Guid> unitIds, CancellationToken cancellationToken);

    /// <summary>The unit named <paramref name="name"/> ignoring case, other than the given one; tracked.</summary>
    Task<InputUnit?> FindByNameAsync(string name, Guid? exceptUnitId, CancellationToken cancellationToken);

    /// <summary>Per unit: how many item units use it and whether a receipt or return line used it (through an item unit).</summary>
    Task<IReadOnlyDictionary<Guid, InputUnitUsage>> UsageAsync(CancellationToken cancellationToken);

    /// <summary>The items whose base unit is this unit, tracked: their <c>Unit</c> name follows a rename.</summary>
    Task<IReadOnlyList<FarmingInputItem>> ItemsWithBaseUnitAsync(Guid unitId, CancellationToken cancellationToken);

    void Add(InputUnit unit);
    void Remove(InputUnit unit);
}

/// <param name="ItemUnits">How many item units use the unit.</param>
/// <param name="OnLines">Whether a receipt or return line names an item unit of it.</param>
public sealed record InputUnitUsage(int ItemUnits, bool OnLines);

public interface IInputItemCategoryRepository
{
    Task<IReadOnlyList<InputItemCategory>> ListAsync(CancellationToken cancellationToken);
    Task<InputItemCategory?> GetForEditAsync(Guid groupId, CancellationToken cancellationToken);
    Task<IReadOnlyList<InputItemCategory>> GetAllForEditAsync(CancellationToken cancellationToken);
    Task<InputItemCategory?> FindByNameAsync(string name, Guid? exceptGroupId, CancellationToken cancellationToken);

    /// <summary>How many items (not deleted) belong to each group; a group with none is absent.</summary>
    Task<IReadOnlyDictionary<Guid, int>> ItemCountsAsync(CancellationToken cancellationToken);

    /// <summary>The deleted items of the group, tracked: they let go of it when the group is deleted.</summary>
    Task<IReadOnlyList<FarmingInputItem>> HiddenItemsAsync(Guid groupId, CancellationToken cancellationToken);

    void Add(InputItemCategory group);
    void Remove(InputItemCategory group);
}

/// <summary>Filters of the item list; null means "any". The period is on the business date of the creation.</summary>
public sealed record InputItemFilter(
    string? Search, InputItemGroup? Group, Guid? ItemGroupId, InputStockState Stock, Guid? SupplierId, DateTimeOffset? CreatedFrom, DateTimeOffset? CreatedBefore,
    int? StatusId, InputItemSort Sort, bool Descending);

public enum InputStockState
{
    All,
    InStock,
    OutOfStock,
    BelowMin,
    AboveMax,
}

public enum InputItemSort
{
    Code,
    Name,
    Group,
    OnHand,
    CostPrice,
    LastPurchasePrice,
    StockValue,
    CreatedAt,
    Status,
}

/// <summary>An item of the list with the figures computed in SQL.</summary>
public sealed record InputItemRow(FarmingInputItem Item, decimal OnHand);

public interface IInputItemListQuery
{
    /// <summary>One page (1-based) of the matching items, and the total and the Σ on hand × cost of every match.</summary>
    Task<(IReadOnlyList<InputItemRow> Rows, int Total, decimal StockValue)> SearchAsync(InputItemFilter filter, int page, int pageSize, CancellationToken cancellationToken);

    Task<IReadOnlyList<InputItemRow>> ListAsync(InputItemFilter filter, CancellationToken cancellationToken);
}


