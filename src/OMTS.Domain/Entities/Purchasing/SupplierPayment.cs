using OMTS.Domain.Common;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Enums;

namespace OMTS.Domain.Entities.Purchasing;

/// <summary>
/// SupplierPayment (SRS 27/10 §I.3.2, PURCHASE-05): money paid to a supplier, or paid back by it. It is not linked to
/// receipts and changes the supplier debt (BR-PURCHASE-04). A recorded payment is never edited: one recorded by mistake is
/// cancelled with a reason and no longer counts (BR-PURCHASE-08).
/// </summary>
public class SupplierPayment : AuditableEntity, IHasStatus
{
    /// <summary>TT000123: one cooperative-wide sequence, whatever the kind, never given twice.</summary>
    public string Code { get; set; } = string.Empty;
    public Guid SupplierId { get; set; }
    public Supplier Supplier { get; set; } = null!;
    public SupplierPaymentKind Kind { get; set; }

    /// <summary>Whole VND, above zero. Not a thousand multiple: BR-PURCHASE-08 sets no such rule.</summary>
    public decimal Amount { get; set; }
    public DateOnly PaymentDate { get; set; }
    public PaymentMethod Method { get; set; }

    /// <summary>Required for a bank transfer, empty for cash; a second payment with the same one needs a confirmation (MSG-PURCHASE-06).</summary>
    public string? BankReference { get; set; }
    public string? Note { get; set; }

    /// <summary>A repeated key returns the first payment and creates nothing.</summary>
    public string? DeduplicationKey { get; set; }
    public Guid RecordedByAccountId { get; set; }
    public int StatusId { get; set; } = StatusIds.SupplierPayment.Recorded;
    public Status Status { get; set; } = null!;
    public DateTimeOffset? CancelledAt { get; set; }
    public string? CancelReason { get; set; }
}
