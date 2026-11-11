using OMTS.Domain.Common;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Platform;

namespace OMTS.Domain.Entities.Sales;

/// <summary>
/// A kind of oysters the cooperative sells by the kg (SALE-11, BR-SALE-05). Its code (SP000001, typed or generated) and its name are unique, discontinued and deleted products included; a Discontinued
/// product is not offered on new requests and invoices and can be resumed (GBR-01). Prices and quantities are per kg, the base unit; other units (lồng, thùng…) carry a factor in kg. The stock of the
/// product comes from harvest lots split per product (<see cref="ProductStockMovement"/>); a product that was used is hidden when deleted, never removed.
/// </summary>
public class Product : AuditableEntity, IHasStatus
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    /// <summary>List price per kg, in whole VND, above 0. Past prices are kept in <see cref="ProductPriceChange"/> and the audit log (BR-SALE-05).</summary>
    public decimal UnitPrice { get; set; }

    /// <summary>The user group ("Nhóm hàng"), optional.</summary>
    public Guid? CategoryId { get; set; }
    public ProductCategory? Category { get; set; }

    public string? Description { get; set; }
    public string? Note { get; set; }

    /// <summary>Reorder level and ceiling in kg, optional.</summary>
    public decimal? MinKg { get; set; }
    public decimal? MaxKg { get; set; }

    /// <summary>A deleted product that had history: no list shows it, its documents still name it.</summary>
    public bool IsHidden { get; set; }

    public ICollection<ProductUnit> Units { get; set; } = [];
    public ICollection<ProductPhoto> Photos { get; set; } = [];

    public int StatusId { get; set; } = StatusIds.Usage.InUse;
    public Status Status { get; set; } = null!;
}
