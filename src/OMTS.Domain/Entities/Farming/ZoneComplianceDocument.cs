using OMTS.Domain.Common;

namespace OMTS.Domain.Entities;

/// <summary>
/// E08 – Legal document that may cover several zones. Renewal creates a new document pointing to the
/// replaced one; “Expiring soon” / “Expired” are derived from <see cref="ExpiryDate"/> (JOB-06).
/// </summary>
public class ZoneComplianceDocument : AuditableEntity
{
    public string DocumentType { get; set; } = string.Empty;
    public string DocumentNumber { get; set; } = string.Empty;
    public string? Holder { get; set; }
    public string? IssuingAuthority { get; set; }
    public string? LegalScope { get; set; }
    public DateOnly? EffectiveDate { get; set; }
    public DateOnly? ExpiryDate { get; set; }
    public string? EvidenceFileUrl { get; set; }

    public Guid? ReplacesDocumentId { get; set; }
    public ZoneComplianceDocument? ReplacesDocument { get; set; }

    public ICollection<DocumentZoneScope> ZoneScopes { get; set; } = [];
}
