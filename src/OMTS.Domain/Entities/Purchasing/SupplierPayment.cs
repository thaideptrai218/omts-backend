using OMTS.Domain.Common;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.CashBook;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Enums;

namespace OMTS.Domain.Entities.Purchasing;

/// <summary>
/// SupplierPayment (SRS 27/10 §I.3.2, PURCHASE-05): money paid to a supplier, or paid back by it; it changes the supplier debt
/// (BR-PURCHASE-04). A payment may name the receipts it pays (<see cref="Allocations"/>); what it does not name is read as covering
/// the oldest receipts. Its money never changes: only its time, payer, method, bank reference and note may be edited (audited); one
/// recorded by mistake is cancelled with a reason and no longer counts (BR-PURCHASE-08).
/// </summary>
public class SupplierPayment : AuditableEntity, IHasStatus
{
    /// <summary>TT000123: one cooperative-wide sequence, whatever the kind, never given twice.</summary>
    public string Code { get; set; } = string.Empty;
    public Guid SupplierId { get; set; }
    public Supplier Supplier { get; set; } = null!;
    public SupplierPaymentKind Kind { get; set; }

    /// <summary>Whole VND, above zero; zero only on a payment with a <see cref="DiscountAmount"/>. Not a thousand multiple: BR-PURCHASE-08 sets no such rule.</summary>
    public decimal Amount { get; set; }

    /// <summary>Whole VND of debt the supplier wrote off on this payment, with no money paid; only on kind Payment.</summary>
    public decimal DiscountAmount { get; set; }

    /// <summary>The purchase return this money came back for, on kind PaidBack only.</summary>
    public Guid? PurchaseReturnId { get; set; }
    public PurchaseReturn? PurchaseReturn { get; set; }
    /// <summary>The business date (UTC+7) of <see cref="PaidAt"/>; the debt and every period filter read it.</summary>
    public DateOnly PaymentDate { get; set; }

    /// <summary>When the money was paid, stored at UTC+7, never later than now.</summary>
    public DateTimeOffset PaidAt { get; set; }

    /// <summary>The Owner or staff member who paid ("Người chi"); the person who recorded it by default.</summary>
    public Guid? PaidByAccountId { get; set; }
    public PaymentMethod Method { get; set; }

    /// <summary>Required for a bank transfer, empty for cash; a second payment with the same one needs a confirmation (MSG-PURCHASE-06).</summary>
    public string? BankReference { get; set; }
    public string? Note { get; set; }

    /// <summary>The bank account the money was paid from or into, on a bank transfer only: an active Bank account of the cash book.</summary>
    public Guid? CashAccountId { get; set; }
    public CashAccount? CashAccount { get; set; }

    /// <summary>A repeated key returns the first payment and creates nothing.</summary>
    public string? DeduplicationKey { get; set; }
    public Guid RecordedByAccountId { get; set; }
    public int StatusId { get; set; } = StatusIds.SupplierPayment.Recorded;
    public Status Status { get; set; } = null!;
    public DateTimeOffset? CancelledAt { get; set; }
    public string? CancelReason { get; set; }

    /// <summary>The Completed receipts of the supplier this payment pays, kind Payment only; set when it is recorded and never changed.</summary>
    public ICollection<SupplierPaymentAllocation> Allocations { get; set; } = [];
}

/// <summary>
/// The part of a payment the Accountant set against one Completed receipt of the same supplier, at most what the receipt had left
/// when the payment was recorded. The ledger covers that receipt with it first; the part of a payment set against no receipt is an
/// advance that covers the oldest receipts.
/// </summary>
public class SupplierPaymentAllocation : Entity
{
    public Guid PaymentId { get; set; }
    public SupplierPayment Payment { get; set; } = null!;
    public Guid ReceiptId { get; set; }
    public PurchaseReceipt Receipt { get; set; } = null!;

    /// <summary>Whole VND above zero.</summary>
    public decimal Amount { get; set; }
}
