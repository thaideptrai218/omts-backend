using OMTS.Domain.Common;

namespace OMTS.Domain.Entities.Identity;

/// <summary>
/// A certification of the cooperative (FARM-01.1), with its number, issuer, expiry date and file. Valid, Expiring soon
/// or Expired is worked out from the expiry date (BR-FARM-06), never stored. Removing one deletes the row; the audit
/// log keeps its values (GBR-02).
/// </summary>
public class CooperativeCertification : AuditableEntity, IHasExpiryReminderMarkers
{
    public Guid CooperativeId { get; set; }
    public Cooperative Cooperative { get; set; } = null!;

    public string Name { get; set; } = string.Empty;
    public string? Number { get; set; }
    public string? Issuer { get; set; }

    /// <summary>Required by FARM-01.1 on every save, not by the database: a row without a date is always Valid.</summary>
    public DateOnly? ExpiryDate { get; set; }

    /// <summary>Storage key of the certificate file (image or PDF).</summary>
    public string? FileUrl { get; set; }

    /// <summary>When the Expiring soon reminder was sent (GFR-03); cleared when the expiry date changes.</summary>
    public DateTimeOffset? ExpiringReminderSentAt { get; set; }

    /// <summary>When the Expired reminder was sent (GFR-03); cleared when the expiry date changes.</summary>
    public DateTimeOffset? ExpiredReminderSentAt { get; set; }
}
