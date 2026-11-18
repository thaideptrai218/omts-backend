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
    /// <summary>The contact address: the names of the province and ward (also what an old free-text entry holds) and their codes of the administrative units list; a code is null for an entry not yet matched. <see cref="Address"/> is the street.</summary>
    public string? Province { get; set; }
    public int? ProvinceCode { get; set; }
    public string? Ward { get; set; }
    public int? WardCode { get; set; }
    public string? Note { get; set; }
    /// <summary>Organization or Individual (a supplier is never a Consumer); the other invoice fields follow it.</summary>
    public InvoiceBuyerType InvoiceBuyerType { get; set; } = InvoiceBuyerType.Individual;
    /// <summary>The invoice name: the company name of an Organization, the person's name of an Individual.</summary>
    public string? CompanyName { get; set; }
    /// <summary>The tax code of an Organization.</summary>
    public string? TaxCode { get; set; }
    /// <summary>Personal data: the personal tax number or ID number (10 or 12 digits) of an Individual; shown on the supplier detail only, never in a list, an export or the audit log.</summary>
    public string? PersonalTaxId { get; set; }
    /// <summary>The invoice address is the contact address; the invoice address fields are then empty.</summary>
    public bool InvoiceAddressSameAsContact { get; set; } = true;
    public string? InvoiceProvinceName { get; set; }
    public int? InvoiceProvinceCode { get; set; }
    public string? InvoiceWardName { get; set; }
    public int? InvoiceWardCode { get; set; }
    public string? InvoiceStreet { get; set; }
    public string? InvoiceEmail { get; set; }
    /// <summary>Storage keys of certificate files (JPEG, PNG or PDF).</summary>
    public List<string> CertificateUrls { get; set; } = [];
    public int StatusId { get; set; } = StatusIds.Usage.InUse;
    public Status Status { get; set; } = null!;
    public ICollection<PurchaseReceipt> Receipts { get; set; } = [];
}
