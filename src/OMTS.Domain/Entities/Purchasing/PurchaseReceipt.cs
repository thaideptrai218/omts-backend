using OMTS.Domain.Common;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Platform;

namespace OMTS.Domain.Entities.Purchasing;

/// <summary>
/// PurchaseReceipt (SRS 27/10 §I.3.2, PURCHASE-01): the goods one supplier delivered at the port. It has no zone: it
/// follows the role only (BR-FARM-08), and each Seed or Material line may name the raft the goods were bought for. A
/// Draft counts nowhere and is removed, never cancelled; a Completed receipt is never changed except by the Owner's
/// correction (PURCHASE-06.2); a Cancelled one keeps its reason (BR-PURCHASE-03, BR-PURCHASE-09).
/// </summary>
public class PurchaseReceipt : AuditableEntity, IHasStatus
{
    /// <summary>PN000123 (GBR-04): one cooperative-wide sequence, given at the first save.</summary>
    public string Code { get; set; } = string.Empty;
    public Guid SupplierId { get; set; }
    public Supplier Supplier { get; set; } = null!;

    /// <summary>The date received: today by default, never later (BR-PURCHASE-01).</summary>
    public DateOnly ReceiptDate { get; set; }

    /// <summary>Free note of the cooperative (not in the SRS; kept for the demo data and the users).</summary>
    public string? Notes { get; set; }
    public Guid RecordedByAccountId { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public string? CancelReason { get; set; }
    public int StatusId { get; set; } = StatusIds.PurchaseReceipt.Draft;
    public Status Status { get; set; } = null!;
    public ICollection<PurchaseReceiptLine> Lines { get; set; } = [];
    public ICollection<PurchaseReturn> Returns { get; set; } = [];

    /// <summary>Payments still allocated to the receipt; they stop being tied to receipts with PURCHASE-05 (BR-PURCHASE-04).</summary>
    public ICollection<SupplierPayment> Payments { get; set; } = [];
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

    /// <summary>Quantity × unit price rounded to the dong, never to thousands (BR-PURCHASE-02); 0 while incomplete.</summary>
    public decimal LineAmount { get; set; }

    /// <summary>The supplier's lot code, on Seed lines only.</summary>
    public string? SupplierLotCode { get; set; }

    /// <summary>The raft the goods were bought for, on Seed and Material lines only (BR-FARM-05, GBR-04).</summary>
    public Guid? RaftId { get; set; }
    public Raft? Raft { get; set; }
}
