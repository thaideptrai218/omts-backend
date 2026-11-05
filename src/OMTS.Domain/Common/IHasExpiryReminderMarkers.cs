namespace OMTS.Domain.Common;

/// <summary>
/// A record reminded before and after its expiry date (GFR-03: zone licenses and cooperative certifications). The markers
/// say which notice was sent, so each one goes out once; both are cleared when the expiry date changes.
/// </summary>
public interface IHasExpiryReminderMarkers
{
    DateOnly? ExpiryDate { get; }

    /// <summary>When the Expiring soon notice (MSG-FARM-01) was sent.</summary>
    DateTimeOffset? ExpiringReminderSentAt { get; set; }

    /// <summary>When the Expired notice (MSG-FARM-02) was sent.</summary>
    DateTimeOffset? ExpiredReminderSentAt { get; set; }
}
