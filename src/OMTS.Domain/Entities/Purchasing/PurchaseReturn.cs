using OMTS.Domain.Common;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Platform;

namespace OMTS.Domain.Entities.Purchasing;

/// <summary>
/// PurchaseReturn (SRS 27/10 §I.3.2, PURCHASE-02): goods sent back to the supplier, either from one Completed receipt or on their
/// own with no receipt code. A Draft counts nowhere and is removed, never cancelled. A Recorded return lowers the debt of
/// <see cref="SupplierId"/> on its <see cref="ReturnDate"/>; its time, reason, discount, quantities and prices may be edited
/// (audited), never below the money already paid back for it. The Owner cancels one recorded by mistake, with a reason, and it then counts nowhere
/// (BR-PURCHASE-07). Money the supplier gives back is a supplier payment of kind PaidBack.
/// </summary>
public class PurchaseReturn : AuditableEntity, IHasStatus
{
    /// <summary>TH000123: one cooperative-wide sequence, never given twice.</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Empty on a return made without a receipt.</summary>
    public Guid? ReceiptId { get; set; }
    public PurchaseReceipt? Receipt { get; set; }

    /// <summary>The supplier whose debt it lowers: the supplier of the receipt when it has one.</summary>
    public Guid SupplierId { get; set; }
    public Supplier Supplier { get; set; } = null!;

    /// <summary>For display and filtering only; optional.</summary>
    public Guid? ZoneId { get; set; }
    public FarmingZone? Zone { get; set; }

    /// <summary>The business date (UTC+7) of <see cref="ReturnedAt"/>; the debt and every period filter read it.</summary>
    public DateOnly ReturnDate { get; set; }

    /// <summary>When the goods were sent back, stored at UTC+7: the time it was saved or completed by default, never later than now.</summary>
    public DateTimeOffset ReturnedAt { get; set; }

    /// <summary>Required to complete, may be empty on a Draft.</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>Whole VND taken off the whole return; it lowers the debt by the lines' total less this.</summary>
    public decimal Discount { get; set; }
    public Guid RecordedByAccountId { get; set; }
    public int StatusId { get; set; } = StatusIds.PurchaseReturn.Recorded;
    public Status Status { get; set; } = null!;
    public DateTimeOffset? CancelledAt { get; set; }
    public string? CancelReason { get; set; }
    public ICollection<PurchaseReturnLine> Lines { get; set; } = [];

    /// <summary>Money the supplier gave back for this return, kept when the return is cancelled.</summary>
    public ICollection<SupplierPayment> Refunds { get; set; } = [];
}

public class PurchaseReturnLine : Entity
{
    public Guid ReturnId { get; set; }
    public PurchaseReturn Return { get; set; } = null!;

    /// <summary>The receipt line it came from; empty on a return made without a receipt.</summary>
    public Guid? ReceiptLineId { get; set; }
    public PurchaseReceiptLine? ReceiptLine { get; set; }
    public Guid ItemId { get; set; }
    public FarmingInputItem Item { get; set; } = null!;

    /// <summary>Copied from the item or the receipt line when saved.</summary>
    public string Unit { get; set; } = string.Empty;
    public decimal Quantity { get; set; }

    /// <summary>
    /// Whole VND, the price each unit is returned at. From a receipt it may be entered; by default it is what one unit of the
    /// receipt line was paid: its unit price less its line discount, less its share of the receipt discount (prorated by line
    /// amount), half up to the dong. Entered on a return made without a receipt.
    /// </summary>
    public decimal UnitPrice { get; set; }

    /// <summary>
    /// The amount when it was recorded: quantity × unit price of the line that day, half up to the dong. It is stored and a
    /// later correction of the receipt does not change it (PM 03/11); an edit of the return's quantity does.
    /// </summary>
    public decimal ReturnValue { get; set; }
}
