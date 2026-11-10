using OMTS.Domain.Common;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Platform;

namespace OMTS.Domain.Entities.Purchasing;

/// <summary>
/// PurchaseReceipt (SRS 27/10 §I.3.2, PURCHASE-01): the goods one supplier delivered at the port. Access follows the role
/// (BR-FARM-08). The zone and the rafts of the lines are kept from older receipts and may still be given, but are no longer
/// needed: what a raft costs comes from the material used on it. A Draft counts nowhere and is removed, never cancelled; a
/// Completed receipt may be edited (its supplier never changes, every edit is audited); a Cancelled one keeps its reason and is
/// never changed (BR-PURCHASE-03, BR-PURCHASE-09).
/// </summary>
public class PurchaseReceipt : AuditableEntity, IHasStatus
{
    /// <summary>PN000123 (GBR-04): one cooperative-wide sequence, given at the first save.</summary>
    public string Code { get; set; } = string.Empty;
    public Guid SupplierId { get; set; }
    public Supplier Supplier { get; set; } = null!;

    /// <summary>The business date (UTC+7) of <see cref="ReceivedAt"/>; every period filter reads it (BR-PURCHASE-01).</summary>
    public DateOnly ReceiptDate { get; set; }

    /// <summary>When the goods were received, stored at UTC+7: now by default, never later.</summary>
    public DateTimeOffset ReceivedAt { get; set; }

    /// <summary>The Owner or staff member who took the goods in: the person who recorded the receipt by default.</summary>
    public Guid? ReceivedByAccountId { get; set; }

    /// <summary>Free note of the cooperative (not in the SRS; kept for the demo data and the users).</summary>
    public string? Notes { get; set; }

    /// <summary>Whole VND taken off the whole receipt; what it adds to the debt is the lines' total less this.</summary>
    public decimal Discount { get; set; }

    /// <summary>The zone the goods are for, for display and filtering only; optional.</summary>
    public Guid? ZoneId { get; set; }
    public FarmingZone? Zone { get; set; }
    public Guid RecordedByAccountId { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public string? CancelReason { get; set; }
    public int StatusId { get; set; } = StatusIds.PurchaseReceipt.Draft;
    public Status Status { get; set; } = null!;
    public ICollection<PurchaseReceiptLine> Lines { get; set; } = [];
    public ICollection<PurchaseReturn> Returns { get; set; } = [];
}

/// <summary>
/// One item received on a purchase receipt. A Seed line of a Completed receipt is a seed lot (GBR-04), coded with the
/// receipt code and <see cref="LineNumber"/> (PN000123-1); seeding sessions point to it. A Draft line may be incomplete:
/// item, quantity and unit price are then empty until the receipt is completed (BR-PURCHASE-03).
/// </summary>
public class PurchaseReceiptLine : Entity
{
    public Guid ReceiptId { get; set; }
    public PurchaseReceipt Receipt { get; set; } = null!;

    /// <summary>1…n in the order entered, unique on the receipt.</summary>
    public int LineNumber { get; set; }
    public Guid? ItemId { get; set; }
    public FarmingInputItem? Item { get; set; }

    /// <summary>Copied from the item when saved; empty while the line has no item.</summary>
    public string Unit { get; set; } = string.Empty;
    public decimal? Quantity { get; set; }

    /// <summary>Whole VND, 0 or more.</summary>
    public decimal? UnitPrice { get; set; }

    /// <summary>Whole VND taken off each unit, 0 to <see cref="UnitPrice"/>.</summary>
    public decimal Discount { get; set; }

    /// <summary>Quantity × (unit price − discount) rounded half up to the dong, never to thousands (BR-PURCHASE-02); 0 while incomplete.</summary>
    public decimal LineAmount { get; set; }

    /// <summary>The supplier's lot code, on Seed lines only.</summary>
    public string? SupplierLotCode { get; set; }

    /// <summary>The raft the goods were bought for, on Seed and Material lines only; optional and not used for the raft material cost.</summary>
    public Guid? RaftId { get; set; }
    public Raft? Raft { get; set; }
}
