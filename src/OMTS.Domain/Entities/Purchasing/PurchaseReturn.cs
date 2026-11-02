using OMTS.Domain.Common;
using OMTS.Domain.Enums;

namespace OMTS.Domain.Entities.Purchasing;

/// <summary>
/// Goods returned to the supplier from a Completed receipt (INP-05), settled as a refund received, a debt
/// reduction, or both. Never edited or deleted.
/// </summary>
public class PurchaseReturn : AuditableEntity
{
    /// <summary>e.g. PRT-20261022-001.</summary>
    public string Code { get; set; } = string.Empty;
    public Guid ReceiptId { get; set; }
    public PurchaseReceipt Receipt { get; set; } = null!;
    public DateOnly ReturnDate { get; set; }
    public string Reason { get; set; } = string.Empty;
    public decimal RefundAmount { get; set; }
    public PaymentMethod? RefundMethod { get; set; }
    public decimal DebtReductionAmount { get; set; }
    public List<string> EvidenceUrls { get; set; } = [];
    public Guid RecordedByAccountId { get; set; }
    public ICollection<PurchaseReturnLine> Lines { get; set; } = [];
}
