using OMTS.Domain.Common;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Enums;

namespace OMTS.Domain.Entities.Purchasing;

/// <summary>A line of a purchase receipt; a seed line gets one seed lot when the receipt is completed.</summary>
public class PurchaseReceiptLine : Entity
{
    public Guid ReceiptId { get; set; }
    public PurchaseReceipt Receipt { get; set; } = null!;
    public Guid ItemId { get; set; }
    public FarmingInputItem Item { get; set; } = null!;
    /// <summary>Copied from the item when saved.</summary>
    public string Unit { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    /// <summary>Quantity × unit price, rounded to the nearest 1,000 VND.</summary>
    public decimal LineAmount { get; set; }
    public string? SupplierLotCode { get; set; }
    public string? ActualSource { get; set; }
    public SeedReceiptLot? SeedLot { get; set; }
}
