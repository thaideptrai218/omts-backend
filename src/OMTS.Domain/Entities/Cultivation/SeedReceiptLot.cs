using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Common;

namespace OMTS.Domain.Entities.Cultivation;

/// <summary>E15 – Actual source, quantity, unit and evidence of one seed receipt (BR-04).</summary>
public class SeedReceiptLot : AuditableEntity
{
    public Guid SeedTypeId { get; set; }
    public FarmingInputItem SeedType { get; set; } = null!;

    public Guid SupplierId { get; set; }
    public Supplier Supplier { get; set; } = null!;
    /// <summary>The seed line of the purchase receipt that created this lot (one lot per line, INP-03).</summary>
    public Guid PurchaseReceiptLineId { get; set; }
    public PurchaseReceiptLine PurchaseReceiptLine { get; set; } = null!;

    public string InternalCode { get; set; } = string.Empty;
    public string? SupplierLotCode { get; set; }
    public string? ActualSource { get; set; }

    /// <summary>Quantity in the original unit; never converted into kg of oysters sold.</summary>
    public decimal Quantity { get; set; }
    public string Unit { get; set; } = string.Empty;

    public DateTimeOffset ReceivedAt { get; set; }
    public List<string> EvidenceUrls { get; set; } = [];
}
