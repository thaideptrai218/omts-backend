using OMTS.Domain.Common;

namespace OMTS.Application.Features.Farming.Licenses;

/// <summary>The two expiry notices of GFR-03.</summary>
public enum ExpiryNotice
{
    /// <summary>MSG-FARM-01.</summary>
    ExpiringSoon,

    /// <summary>MSG-FARM-02.</summary>
    Expired,
}

/// <summary>
/// GFR-03 for zone licenses and cooperative certifications: one notice when the record becomes Expiring soon and one when
/// it becomes Expired, each once (markers on the record). A record found already Expired gets only the Expired notice;
/// a new expiry date re-arms both.
/// </summary>
public static class ExpiryReminderRule
{
    /// <summary>The notice due for a record that is <paramref name="expiringSoon"/> or <paramref name="expired"/> today, or null.</summary>
    public static ExpiryNotice? Due(bool expiringSoon, bool expired, IHasExpiryReminderMarkers record) =>
        expired && record.ExpiredReminderSentAt is null ? ExpiryNotice.Expired
            : expiringSoon && record.ExpiringReminderSentAt is null ? ExpiryNotice.ExpiringSoon
            : null;

    /// <summary>Records that <paramref name="notice"/> was sent; after the Expired notice no Expiring soon one follows.</summary>
    public static void MarkSent(IHasExpiryReminderMarkers record, ExpiryNotice notice, DateTimeOffset now)
    {
        if (notice == ExpiryNotice.Expired)
        {
            record.ExpiredReminderSentAt = now;
        }

        record.ExpiringReminderSentAt ??= now;
    }

    /// <summary>Clears both markers when the expiry date is about to change to <paramref name="newExpiryDate"/>.</summary>
    public static void RearmOnNewExpiry(IHasExpiryReminderMarkers record, DateOnly? newExpiryDate)
    {
        if (record.ExpiryDate != newExpiryDate)
        {
            record.ExpiringReminderSentAt = null;
            record.ExpiredReminderSentAt = null;
        }
    }
}
