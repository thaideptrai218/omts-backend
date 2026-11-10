using OMTS.Domain.Common;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Enums;

namespace OMTS.Domain.Entities.Purchasing;

/// <summary>
/// Supplier (PURCHASE-07): a party that delivers farming inputs, on one cooperative-wide list with no zone
/// (BR-PURCHASE-06). Discontinued instead of deleted, and can be resumed (GBR-01).
/// </summary>
public class Supplier : AuditableEntity, IHasStatus
{
    /// <summary>NCC000123: one cooperative-wide sequence, never given twice.</summary>
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public SupplierType SupplierType { get; set; }
    /// <summary>Unique among suppliers, discontinued ones included (business identity).</summary>
    public string PhoneNumber { get; set; } = string.Empty;
    public string? Address { get; set; }
    /// <summary>Optional, stored trimmed and lower-case.</summary>
    public string? Email { get; set; }
    public string? Province { get; set; }
    public string? Ward { get; set; }
    public string? Note { get; set; }
    /// <summary>Company name for invoices.</summary>
    public string? CompanyName { get; set; }
    public string? TaxCode { get; set; }
    /// <summary>Personal data (CCCD): shown on the supplier detail only, never in a list, an export or the audit log.</summary>
    public string? IdentityNumber { get; set; }
    /// <summary>Storage keys of certificate files (JPEG, PNG or PDF).</summary>
    public List<string> CertificateUrls { get; set; } = [];
    public int StatusId { get; set; } = StatusIds.Usage.InUse;
    public Status Status { get; set; } = null!;
    public ICollection<PurchaseReceipt> Receipts { get; set; } = [];
}
