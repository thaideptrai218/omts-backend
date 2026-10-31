using OMTS.Domain.Common;

namespace OMTS.Domain.Entities.Identity;

/// <summary>
/// A certification of the cooperative (FARM-01.1), with its number, issuer, expiry date and file. Valid, Expiring soon
/// or Expired is worked out from the expiry date (BR-FARM-06), never stored. Removing one deletes the row; the audit
/// log keeps its values (GBR-02).
/// </summary>
public class CooperativeCertification : AuditableEntity
{
    public Guid CooperativeId { get; set; }
    public Cooperative Cooperative { get; set; } = null!;

    public string Name { get; set; } = string.Empty;
    public string? Number { get; set; }
    public string? Issuer { get; set; }

    /// <summary>Empty for a certification that does not expire: it is always Valid.</summary>
    public DateOnly? ExpiryDate { get; set; }

    /// <summary>Storage key of the certificate file (image or PDF).</summary>
    public string? FileUrl { get; set; }
}
