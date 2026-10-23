using OMTS.Domain.Common;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Enums;

namespace OMTS.Domain.Entities.Purchasing;

/// <summary>
/// E14 – Supplier of oyster seed, raft materials or other farming inputs: one cooperative-wide list used by
/// purchase receipts and supplier payments (INP-01). Discontinued instead of deleted.
/// </summary>
public class Supplier : AuditableEntity, IHasStatus
{
    public string Name { get; set; } = string.Empty;
    public SupplierType SupplierType { get; set; }
    /// <summary>Unique among suppliers.</summary>
    public string PhoneNumber { get; set; } = string.Empty;
    /// <summary>Address, email, Zalo, contact person.</summary>
    public string? OtherContacts { get; set; }
    /// <summary>Storage keys of certificate files (JPEG, PNG or PDF).</summary>
    public List<string> CertificateUrls { get; set; } = [];
    public int StatusId { get; set; } = StatusIds.Usage.InUse;
    public Status Status { get; set; } = null!;
    public ICollection<PurchaseReceipt> Receipts { get; set; } = [];
}
