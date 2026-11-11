using OMTS.Domain.Common;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Enums;

namespace OMTS.Domain.Entities.Sales;

/// <summary>
/// A unit a product is counted in; the unit itself is a row of the cooperative's shared unit list (<see cref="InputUnit"/>). <see cref="Factor"/> is how many kg one of it holds; the base unit is kg with factor 1.
/// The code is unique across all product units and also differs from every product code.
/// </summary>
public class ProductUnit : Entity
{
    public Guid ProductId { get; set; }
    public Product Product { get; set; } = null!;
    public Guid UnitId { get; set; }
    public InputUnit Unit { get; set; } = null!;
    public decimal Factor { get; set; } = 1m;
    public string Code { get; set; } = string.Empty;
    public bool IsBase { get; set; }

    /// <summary>The price of one of this unit, whole VND above 0. The base unit's price is <see cref="Product.UnitPrice"/>, kept equal to it; an extra unit starts at factor × the base price and is then its own.</summary>
    public decimal SalePrice { get; set; }
}

/// <summary>A user group of products ("Nhóm hàng"), ordered by <see cref="SortOrder"/>; two levels at most (a parent is never itself a child). Deleted only when it has no child group and no product.</summary>
public class ProductCategory : AuditableEntity
{
    public string Name { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public Guid? ParentId { get; set; }
    public ProductCategory? Parent { get; set; }
}

/// <summary>A photo of a product (at most five), kept in file storage under <see cref="StorageKey"/>.</summary>
public class ProductPhoto : Entity
{
    public Guid ProductId { get; set; }
    public Product Product { get; set; } = null!;
    public string StorageKey { get; set; } = string.Empty;
    public int SortOrder { get; set; }
}

/// <summary>One row of the price history of a product: written for each unit of the product when it is created (no old price) and every time the price of a unit changes.</summary>
public class ProductPriceChange : Entity
{
    public Guid ProductId { get; set; }
    public Product Product { get; set; } = null!;

    /// <summary>The shared unit the price is for (kg for the list price) and its code and name as they were; no foreign key, so the unit can be removed from the product later.</summary>
    public Guid UnitId { get; set; }
    public string UnitCode { get; set; } = string.Empty;
    public string UnitName { get; set; } = string.Empty;
    public decimal? OldPrice { get; set; }
    public decimal NewPrice { get; set; }
    public DateTimeOffset ChangedAt { get; set; }
    public Guid? ChangedByAccountId { get; set; }
}

/// <summary>
/// One row of the append-only stock ledger of a product, in kg. The quantity on hand of a product is the sum of its rows; that of a harvest lot the sum of the rows naming the lot. A document that is edited
/// or cancelled never changes its rows: a <see cref="ProductStockMovementKind.Reversal"/> row with the opposite quantity (naming the row it undoes in <see cref="ReversesMovementId"/>) is added, then the new rows.
/// The lot is named by id and code only, with no foreign key, so a lot removed later leaves its rows and their reversals whole.
/// </summary>
public class ProductStockMovement : Entity
{
    /// <summary>Write order; breaks ties between rows with the same time.</summary>
    public long Sequence { get; set; }

    public Guid ProductId { get; set; }
    public Product Product { get; set; } = null!;

    public Guid? HarvestLotId { get; set; }
    public string? LotCode { get; set; }

    public ProductStockMovementKind Kind { get; set; }

    /// <summary>The harvest lot (for a harvest row), sales invoice or disposal.</summary>
    public Guid? DocumentId { get; set; }
    public string DocumentCode { get; set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; set; }

    /// <summary>Signed kg: positive adds to the stock.</summary>
    public decimal QuantityKg { get; set; }

    /// <summary>The price of one kg on the document (the invoice line price); null on rows with none.</summary>
    public decimal? TransactionPrice { get; set; }

    /// <summary>The customer of a sale, or the raft of a harvest.</summary>
    public string? PartyName { get; set; }
    public string? PartyCode { get; set; }

    public Guid? ReversesMovementId { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
    public Guid? RecordedByAccountId { get; set; }
}
