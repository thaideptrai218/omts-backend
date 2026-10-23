using OMTS.Domain.Common;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Enums;

namespace OMTS.Domain.Entities.Purchasing;

/// <summary>
/// Money paid to a supplier against a receipt (INP-06), or the paid-now amount of a receipt (INP-03). A
/// payment kept when its receipt is cancelled is detached (<see cref="ReceiptId"/> null) until the
/// Accountant allocates it to another receipt of the same supplier.
/// </summary>
public class SupplierPayment : AuditableEntity, IHasStatus
{
    public Guid SupplierId { get; set; }
    public Supplier Supplier { get; set; } = null!;
    public Guid? ReceiptId { get; set; }
    public PurchaseReceipt? Receipt { get; set; }
    public decimal Amount { get; set; }
    public DateOnly PaymentDate { get; set; }
    public PaymentMethod Method { get; set; }
    public string? ReferenceCode { get; set; }
    public List<string> EvidenceUrls { get; set; } = [];
    /// <summary>A repeated key returns the first payment and creates nothing.</summary>
    public string? DeduplicationKey { get; set; }
    public Guid RecordedByAccountId { get; set; }
    public int StatusId { get; set; } = StatusIds.SupplierPayment.Recorded;
    public Status Status { get; set; } = null!;
    public string? CancelReason { get; set; }
}
