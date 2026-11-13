using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Purchasing.Items;

/// <summary>
/// A unit of the item on the form. <see cref="Id"/> names an existing row of the item (a row without it is new); the catalogue unit is <see cref="UnitId"/>, or <see cref="UnitName"/>
/// (found in the catalogue or created there). The factor is how many base units this unit holds; the base unit is stored with factor 1.
/// A <see cref="Code"/> left empty becomes the item code, "-" and the first letter of the unit name.
/// </summary>
public sealed record ItemUnitInput(Guid? Id, Guid? UnitId, string? UnitName, decimal Factor, string? Code, bool IsBase);

/// <summary>
/// The item form. A save replaces every field: what the request leaves out is cleared, except <paramref name="Code"/> (empty keeps the stored code of an item or generates the next one for a
/// new item) and <paramref name="Units"/> (omitted on an update keeps the units; on a create <paramref name="Unit"/> names the base unit).
/// </summary>
/// <param name="Code">1–30 ASCII letters, digits, "-", "_" or "."; unique among item codes and unit codes regardless of letter case.</param>
/// <param name="Group">The type: Seed, Material or Other; fixed once the item is used.</param>
/// <param name="ItemGroupId">A user group, optional.</param>
/// <param name="OpeningQuantity">Base units on hand when the item is created, or the correction of the opening quantity while it is the item's only movement.</param>
/// <param name="Unit">The base unit by name when <paramref name="Units"/> is omitted on a create.</param>
public sealed record SaveFarmingInputItemRequest(
    string? Code,
    string? Name,
    InputItemGroup? Group,
    string? Unit,
    string? Description,
    string? RowVersion = null,
    Guid? ItemGroupId = null,
    string? Notes = null,
    decimal? MinQuantity = null,
    decimal? MaxQuantity = null,
    decimal? CostPrice = null,
    decimal? OpeningQuantity = null,
    IReadOnlyList<ItemUnitInput>? Units = null);

/// <param name="IsUsed">On a receipt or return line (or planned by a cultivation cycle): the factor and unit of this item unit are fixed and it cannot be removed.</param>
public sealed record ItemUnitView(Guid Id, Guid UnitId, string Name, decimal Factor, string Code, bool IsBase, bool IsUsed);

public sealed record ItemPhotoView(Guid Id, string Url);

/// <param name="Unit">The name of the base unit.</param>
/// <param name="PhotoUrl">A temporary address of the first photo; null without one.</param>
/// <param name="OnHand">Σ of the movements, base units.</param>
/// <param name="StockValue">On hand × cost price; 0 without a cost price.</param>
/// <param name="IsUsed">On a receipt line or a material usage record: the type is then fixed.</param>
public sealed record FarmingInputItemListItem(
    Guid Id,
    string Code,
    string Name,
    InputItemGroup Group,
    Guid? ItemGroupId,
    string? ItemGroupName,
    string Unit,
    IReadOnlyList<ItemUnitView> Units,
    string? Description,
    string? Notes,
    string? PhotoUrl,
    decimal? MinQuantity,
    decimal? MaxQuantity,
    decimal? CostPrice,
    decimal? LastPurchasePrice,
    decimal OnHand,
    decimal StockValue,
    bool IsUsed,
    int StatusId,
    string Status,
    DateTimeOffset CreatedAt,
    string? CreatedBy,
    string RowVersion);

/// <summary>The detail of an item: every column of the list plus every photo.</summary>
/// <param name="HasMovements">A receipt, return, material usage or movement names the item: the base unit is then fixed.</param>
/// <param name="OpeningQuantity">The quantity of the opening movement; null without one.</param>
public sealed record FarmingInputItemView(
    Guid Id,
    string Code,
    string Name,
    InputItemGroup Group,
    Guid? ItemGroupId,
    string? ItemGroupName,
    string Unit,
    IReadOnlyList<ItemUnitView> Units,
    string? Description,
    string? Notes,
    string? PhotoUrl,
    decimal? MinQuantity,
    decimal? MaxQuantity,
    decimal? CostPrice,
    decimal? LastPurchasePrice,
    decimal OnHand,
    decimal StockValue,
    bool IsUsed,
    int StatusId,
    string Status,
    DateTimeOffset CreatedAt,
    string? CreatedBy,
    string RowVersion,
    IReadOnlyList<ItemPhotoView> Photos,
    bool HasMovements,
    decimal? OpeningQuantity)
{
    public static FarmingInputItemView Of(FarmingInputItemListItem i, IReadOnlyList<ItemPhotoView> photos, bool hasMovements, decimal? openingQuantity) => new(
        i.Id, i.Code, i.Name, i.Group, i.ItemGroupId, i.ItemGroupName, i.Unit, i.Units, i.Description, i.Notes, photos.Count > 0 ? photos[0].Url : null, i.MinQuantity, i.MaxQuantity,
        i.CostPrice, i.LastPurchasePrice, i.OnHand, i.StockValue, i.IsUsed, i.StatusId, i.Status, i.CreatedAt, i.CreatedBy, i.RowVersion, photos, hasMovements, openingQuantity);
}
public enum InputItemStatusFilter
{
    InUse,
    Discontinued,
    All,
}

/// <summary>The item list and export query. The period is on the business date of the creation.</summary>
/// <param name="Search">Text in the code, the name or any unit code.</param>
/// <param name="Group">The type: Seed, Material or Other.</param>
/// <param name="ItemGroupId">A user group.</param>
/// <param name="Stock">All, InStock (on hand above 0), OutOfStock (0 or below), BelowMin or AboveMax.</param>
/// <param name="SupplierId">Items on a Completed receipt of this supplier.</param>
/// <param name="Status">InUse (the default), Discontinued or All.</param>
/// <param name="SortBy">code (the default, newest first), name, group, onHand, costPrice, lastPurchasePrice, stockValue, createdAt or status.</param>
/// <param name="SortDir">asc or desc; the default is desc for the code and asc for the others.</param>
public sealed record FarmingInputItemListQuery(
    string? Search = null,
    InputItemGroup? Group = null,
    Guid? ItemGroupId = null,
    InputStockState Stock = InputStockState.All,
    Guid? SupplierId = null,
    DateOnly? CreatedFrom = null,
    DateOnly? CreatedTo = null,
    InputItemStatusFilter Status = InputItemStatusFilter.InUse,
    string? SortBy = null,
    string? SortDir = null,
    int Page = 1,
    int PageSize = 20);

/// <param name="StockValue">Σ on hand × cost price of every match.</param>
public sealed record FarmingInputItemListSummary(decimal StockValue);

public sealed record FarmingInputItemPage(IReadOnlyList<FarmingInputItemListItem> Items, int Page, int PageSize, int Total, FarmingInputItemListSummary Summary);

/// <summary>What the Add item form starts with.</summary>
/// <param name="Code">The proposed code; not reserved, the Owner may replace it.</param>
public sealed record NewFarmingInputItemProposal(string Code);

public sealed record InputItemImportError(int Row, IReadOnlyList<string> Fields, string Message);

public sealed record InputItemImportResult(int Imported, IReadOnlyList<InputItemImportError> Errors);

public sealed record InputUnitView(Guid Id, string Name, int ItemCount, bool IsUsed, string RowVersion);

public sealed record SaveInputUnitRequest(string? Name, string? RowVersion = null);

/// <param name="ItemCount">The items of the group; a parent group also counts the items of its children.</param>
/// <param name="ParentId">The parent group; null for a top-level group.</param>
public sealed record InputItemGroupView(Guid Id, string Name, int SortOrder, int ItemCount, string RowVersion, Guid? ParentId = null);

/// <param name="ParentId">The top-level group this one sits under, or null for a top-level group. A group that has children cannot get a parent.</param>
public sealed record SaveInputItemGroupRequest(string? Name, string? RowVersion = null, Guid? ParentId = null);

public sealed record ReorderInputItemGroupsRequest(IReadOnlyList<Guid>? Ids);

public sealed record InputInventorySettingsView(InputCostMethod CostMethod, bool AllowNegativeQuantity, string RowVersion);

public sealed record UpdateInputInventorySettingsRequest(InputCostMethod? CostMethod, bool? AllowNegativeQuantity, string? RowVersion);

/// <summary>A row of the price setup of a receipt: the price of this receipt per base unit with the item's cost and last purchase price.</summary>
public sealed record ReceiptPriceSetupLine(Guid ItemId, string ItemCode, string ItemName, string Unit, decimal? ReceiptPrice, decimal? CostPrice, decimal? LastPurchasePrice);

public sealed record ReceiptPriceSetupView(Guid ReceiptId, string ReceiptCode, IReadOnlyList<ReceiptPriceSetupLine> Lines);

public sealed record ItemCostInput(Guid? ItemId, decimal? CostPrice);

public sealed record UpdateReceiptPriceSetupRequest(IReadOnlyList<ItemCostInput>? Items);

