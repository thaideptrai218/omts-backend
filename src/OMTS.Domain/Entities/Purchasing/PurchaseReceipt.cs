using OMTS.Domain.Common;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Enums;

namespace OMTS.Domain.Entities.Purchasing;

/// <summary>
/// One purchase from one supplier into one zone, optionally for one raft (INP-03). A Draft does not count;
/// completing it creates the seed lots and the paid-now payment. A Completed receipt is never edited: it is
/// cancelled and copied (INP-04). Money is whole VND.
/// </summary>
public class PurchaseReceipt : AuditableEntity, IHasStatus
{
    /// <summary>e.g. PR-20261022-001.</summary>
    public string Code { get; set; } = string.Empty;
    public Guid SupplierId { get; set; }
    public Supplier Supplier { get; set; } = null!;
    public Guid ZoneId { get; set; }
    public FarmingZone Zone { get; set; } = null!;
    /// <summary>Required when a line is a raft material; the raft belongs to <see cref="ZoneId"/>.</summary>
    public Guid? RaftId { get; set; }
    public Raft? Raft { get; set; }
    public DateOnly ReceiptDate { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal AmountPaidNow { get; set; }
    public PaymentMethod? PaidNowMethod { get; set; }
    public List<string> InvoicePhotoUrls { get; set; } = [];
    public string? Notes { get; set; }
    public Guid RecordedByAccountId { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public string? CancelReason { get; set; }
    public Guid? CopiedFromReceiptId { get; set; }
    public int StatusId { get; set; } = StatusIds.PurchaseReceipt.Draft;
    public Status Status { get; set; } = null!;
    public ICollection<PurchaseReceiptLine> Lines { get; set; } = [];
    public ICollection<PurchaseReturn> Returns { get; set; } = [];
    public ICollection<SupplierPayment> Payments { get; set; } = [];
}
