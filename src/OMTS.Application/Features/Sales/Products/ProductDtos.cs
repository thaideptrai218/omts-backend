using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Sales.Products;

/// <summary>
/// An extra unit of the product on the form (the base unit, kg, is always there). <see cref="Id"/> names an existing row of the product (a row without it is new); the catalogue unit is <see cref="UnitId"/>,
/// or <see cref="UnitName"/> (found in the shared unit list or created there). <see cref="Factor"/> is how many kg this unit holds. A <see cref="Code"/> left empty becomes the product code, "-" and the first letter of the unit name.
/// <see cref="SalePrice"/> is the price of one of this unit (whole VND above 0). Left empty it is factor × the price per kg for a new unit, and what it was for an existing one; once set it is its own price.
/// </summary>
public sealed record ProductUnitInput(Guid? Id, Guid? UnitId, string? UnitName, decimal Factor, string? Code, decimal? SalePrice = null);

/// <summary>
/// The product form. A save replaces every field: what the request leaves out is cleared, except <paramref name="Code"/> (empty keeps the stored code of a product or generates the next one for a new product)
/// and <paramref name="Units"/> (omitted on an update keeps the units; on a create the product has kg only).
/// </summary>
/// <param name="UnitPrice">List price per kg, whole VND above 0.</param>
/// <param name="Code">1–30 ASCII letters, digits, "-", "_" or "."; unique among product codes and unit codes regardless of letter case.</param>
/// <param name="CategoryId">A user group, optional.</param>
/// <param name="Units">The extra units (lồng, thùng…) with their factor in kg; one not listed is removed.</param>
public sealed record SaveProductRequest(
    string? Name,
    decimal? UnitPrice,
    string? RowVersion = null,
    string? Code = null,
    Guid? CategoryId = null,
    string? Description = null,
    string? Note = null,
    decimal? MinKg = null,
    decimal? MaxKg = null,
    IReadOnlyList<ProductUnitInput>? Units = null);

/// <param name="SalePrice">The price of one of this unit; the base unit (kg) carries the list price. Null for a trader.</param>
public sealed record ProductUnitView(Guid Id, Guid UnitId, string Name, decimal Factor, string Code, bool IsBase, decimal? SalePrice);

public sealed record ProductPhotoView(Guid Id, string Url);

/// <summary>
/// A product as the lists and pickers show it. The first six fields are the original ones; the rest are added by the product catalogue.
/// </summary>
/// <param name="UnitPrice">The list price per kg in VND; null for a trader, who sends requests with no price (BR-SALE-01).</param>
/// <param name="Unit">The name of the base unit (kg).</param>
/// <param name="PhotoUrl">A temporary address of the first photo; null without one (the paged list only).</param>
/// <param name="OnHandKg">Σ of the ledger in kg; null for a trader.</param>
/// <param name="IsUsed">On an invoice line, a purchase request, a harvest lot, a disposal or the ledger: deleting the product then hides it.</param>
public sealed record ProductView(
    Guid Id,
    string Name,
    decimal? UnitPrice,
    int StatusId,
    string Status,
    string RowVersion,
    string Code,
    Guid? CategoryId,
    string? CategoryName,
    string Unit,
    IReadOnlyList<ProductUnitView> Units,
    string? Description,
    string? Note,
    string? PhotoUrl,
    decimal? MinKg,
    decimal? MaxKg,
    decimal? OnHandKg,
    bool IsUsed,
    DateTimeOffset CreatedAt,
    string? CreatedBy);

/// <summary>The detail of a product: every field of the list plus every photo.</summary>
public sealed record ProductDetailView(
    Guid Id,
    string Name,
    decimal? UnitPrice,
    int StatusId,
    string Status,
    string RowVersion,
    string Code,
    Guid? CategoryId,
    string? CategoryName,
    string Unit,
    IReadOnlyList<ProductUnitView> Units,
    string? Description,
    string? Note,
    string? PhotoUrl,
    decimal? MinKg,
    decimal? MaxKg,
    decimal? OnHandKg,
    bool IsUsed,
    DateTimeOffset CreatedAt,
    string? CreatedBy,
    IReadOnlyList<ProductPhotoView> Photos)
{
    public static ProductDetailView Of(ProductView p, IReadOnlyList<ProductPhotoView> photos) => new(
        p.Id, p.Name, p.UnitPrice, p.StatusId, p.Status, p.RowVersion, p.Code, p.CategoryId, p.CategoryName, p.Unit, p.Units, p.Description, p.Note,
        photos.Count > 0 ? photos[0].Url : null, p.MinKg, p.MaxKg, p.OnHandKg, p.IsUsed, p.CreatedAt, p.CreatedBy, photos);
}

/// <summary>A saved product with MSG-GEN-05 (SALE-11 step 5).</summary>
public sealed record ProductSaved(ProductDetailView Product, string Message, string MessageCode);

/// <param name="Removed">True when nothing referred to the product and it was removed for good; false when it had history and was hidden (Discontinued, in no list).</param>
public sealed record ProductDeleted(Guid Id, string Code, bool Removed);

/// <summary>What the Add product form starts with.</summary>
/// <param name="Code">The proposed code; not reserved, the Owner may replace it.</param>
public sealed record NewProductProposal(string Code);

public enum ProductStatusFilter
{
    InUse,
    Discontinued,
    All,
}

/// <summary>The product list and export query.</summary>
/// <param name="Search">Text in the code, the name or any unit code.</param>
/// <param name="CategoryId">A user group; a parent group also lists its children's products.</param>
/// <param name="Stock">All, InStock (kg on hand above 0), OutOfStock (0 or below), BelowMin or AboveMax.</param>
/// <param name="Status">InUse (the default), Discontinued or All.</param>
/// <param name="SortBy">code (the default, newest first), name, category, unitPrice, onHand, createdAt or status.</param>
/// <param name="SortDir">asc or desc; the default is desc for the code and asc for the others.</param>
public sealed record ProductListQuery(
    string? Search = null,
    Guid? CategoryId = null,
    ProductStockState Stock = ProductStockState.All,
    ProductStatusFilter Status = ProductStatusFilter.InUse,
    string? SortBy = null,
    string? SortDir = null,
    int Page = 1,
    int PageSize = 20);

/// <param name="TotalOnHandKg">Σ kg on hand of every match, not only the page.</param>
public sealed record ProductListSummary(decimal TotalOnHandKg);

public sealed record ProductPage(IReadOnlyList<ProductView> Items, int Page, int PageSize, int Total, ProductListSummary Summary);

public sealed record ProductImportError(int Row, IReadOnlyList<string> Fields, string Message);

public sealed record ProductImportResult(int Imported, IReadOnlyList<ProductImportError> Errors);

/// <param name="ProductCount">The products of the group; a parent group also counts the products of its children.</param>
public sealed record ProductCategoryView(Guid Id, string Name, int SortOrder, int ProductCount, string RowVersion, Guid? ParentId = null);

/// <param name="ParentId">The top-level group this one sits under, or null for a top-level group. A group that has children cannot get a parent.</param>
public sealed record SaveProductCategoryRequest(string? Name, string? RowVersion = null, Guid? ParentId = null);

public sealed record ReorderProductCategoriesRequest(IReadOnlyList<Guid>? Ids);

/// <summary>The movements of a product ("Nhập – xuất" tab) in a period, newest first. Reversal rows (an edit or cancellation undoing a row) and the rows they undo are left out unless asked for.</summary>
public sealed record ProductMovementQuery(DateOnly? From = null, DateOnly? To = null, ProductStockMovementKind? Kind = null, bool IncludeReversals = false, int Page = 1, int PageSize = 20);

/// <summary>A movement of a product, kg. <see cref="DocumentKind"/> says what the document is: HarvestLot, SalesInvoice or ProductDisposal (a reversal row names the kind of the row it undoes).</summary>
public sealed record ProductMovementView(
    Guid Id,
    long Sequence,
    ProductStockMovementKind Kind,
    string DocumentKind,
    Guid? DocumentId,
    string DocumentCode,
    DateTimeOffset OccurredAt,
    decimal QuantityKg,
    decimal BalanceAfterKg,
    decimal? TransactionPrice,
    string? PartyName,
    string? PartyCode,
    Guid? HarvestLotId,
    string? LotCode,
    Guid? ReversesMovementId);

public sealed record ProductMovementPage(IReadOnlyList<ProductMovementView> Items, int Page, int PageSize, int Total, string Unit);

/// <summary>The kg of a product on one harvest lot: what the lot gave, what was sold and written off, and what is left. A lot's leftover on close is a note of the lot only.</summary>
public sealed record ProductLotView(
    Guid LotId,
    string LotCode,
    Guid? ZoneId,
    string? ZoneCode,
    string? RaftCode,
    DateOnly? HarvestDate,
    DateTimeOffset? HarvestedAt,
    int? LotStatusId,
    decimal HarvestedKg,
    decimal SoldKg,
    decimal DisposedKg,
    decimal ReturnedKg,
    decimal RemainingKg);

public sealed record ProductLotsView(Guid ProductId, string ProductCode, IReadOnlyList<ProductLotView> Lots, decimal TotalRemainingKg);

public sealed record ProductPriceChangeView(Guid Id, Guid UnitId, string UnitCode, string UnitName, decimal? OldPrice, decimal NewPrice, DateTimeOffset ChangedAt, Guid? ChangedById, string? ChangedBy);
