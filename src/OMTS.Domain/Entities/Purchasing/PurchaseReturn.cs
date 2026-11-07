using OMTS.Domain.Common;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Platform;

namespace OMTS.Domain.Entities.Purchasing;

/// <summary>
/// PurchaseReturn (SRS 27/10 §I.3.2, PURCHASE-02): goods sent back to the supplier from one Completed receipt, after the Owner
/// approved it in person. Dated the day it is recorded and never edited: the Owner cancels one recorded by mistake, with a
/// reason, and it then counts nowhere (BR-PURCHASE-07). Money the supplier gives back is a supplier payment of kind PaidBack.
/// </summary>
public class PurchaseReturn : AuditableEntity, IHasStatus
{
    /// <summary>TH000123: one cooperative-wide sequence, never given twice.</summary>
    public string Code { get; set; } = string.Empty;
    public Guid ReceiptId { get; set; }
    public PurchaseReceipt Receipt { get; set; } = null!;
    public DateOnly ReturnDate { get; set; }
    public string Reason { get; set; } = string.Empty;
    public Guid RecordedByAccountId { get; set; }
    public int StatusId { get; set; } = StatusIds.PurchaseReturn.Recorded;
    public Status Status { get; set; } = null!;
    public DateTimeOffset? CancelledAt { get; set; }
    public string? CancelReason { get; set; }
    public ICollection<PurchaseReturnLine> Lines { get; set; } = [];
}

public class PurchaseReturnLine : Entity
{
    public Guid ReturnId { get; set; }
    public PurchaseReturn Return { get; set; } = null!;
    public Guid ReceiptLineId { get; set; }
    public PurchaseReceiptLine ReceiptLine { get; set; } = null!;
    public decimal Quantity { get; set; }

    /// <summary>
    /// The amount when it was recorded: quantity × unit price of the line that day, half up to the dong. It is stored and a
    /// later correction of the unit price does not change it (PM 03/11).
    /// </summary>
    public decimal ReturnValue { get; set; }
}
