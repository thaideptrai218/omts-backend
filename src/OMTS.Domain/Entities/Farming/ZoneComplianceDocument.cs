using OMTS.Domain.Common;

namespace OMTS.Domain.Entities.Farming;

/// <summary>
/// ZoneLicense of the SRS (FARM-03): a license of one farming zone with its validity dates and file. A newer license can
/// replace it (<see cref="ReplacesDocumentId"/> on the newer one); Not yet effective, Valid, Expiring soon, Expired and
/// Replaced are worked out from the dates and the replacing license (BR-FARM-06), never stored.
/// </summary>
public class ZoneComplianceDocument : AuditableEntity, IHasExpiryReminderMarkers
{
    public Guid ZoneId { get; set; }
    public FarmingZone Zone { get; set; } = null!;

    /// <summary>Optional license type code (not in the SRS); null when not given.</summary>
    public string? DocumentType { get; set; }

    public string DocumentNumber { get; set; } = string.Empty;
    public string? Holder { get; set; }
    public string? IssuingAuthority { get; set; }
    public string? LegalScope { get; set; }
    public DateOnly? EffectiveDate { get; set; }
    public DateOnly? ExpiryDate { get; set; }
    public string? EvidenceFileUrl { get; set; }

    public Guid? ReplacesDocumentId { get; set; }
    public ZoneComplianceDocument? ReplacesDocument { get; set; }

    /// <summary>When the Expiring soon reminder was sent (GFR-03); cleared when the expiry date changes.</summary>
    public DateTimeOffset? ExpiringReminderSentAt { get; set; }

    /// <summary>When the Expired reminder was sent (GFR-03); cleared when the expiry date changes.</summary>
    public DateTimeOffset? ExpiredReminderSentAt { get; set; }
}
