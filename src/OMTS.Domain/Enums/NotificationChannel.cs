namespace OMTS.Domain.Enums;

/// <summary>Channel a notification category can be enabled for (ACS-07).</summary>
public enum NotificationChannel
{
    InApp,
    Push,
    Email,

    /// <summary>Kept for stored data; not offered since 22/10 (Zalo OA replaces SMS for notifications).</summary>
    Sms,

    /// <summary>Zalo Official Account; not configured yet, so never eligible (MSG21).</summary>
    Zalo,
}
