
namespace OMTS.Application.Features.Notifications;

/// <summary>Preference categories a user turns on/off per channel (ACS-07).</summary>
public static class NotificationCategories
{
    public const string Orders = "ORDERS";
    public const string Work = "WORK";
    public const string DeadlineReminders = "DEADLINE_REMINDERS";
    public const string WaterAlerts = "WATER_ALERTS";
    public const string TraceabilityQrSent = "TRACEABILITY_QR_SENT";
    public const string SalesNotices = "SALES_NOTICES";

    public static readonly IReadOnlyList<string> All = [Orders, Work, DeadlineReminders, WaterAlerts, TraceabilityQrSent, SalesNotices];

    /// <summary>Always delivered in-app and by push; cannot be turned off (MSG23).</summary>
    public static readonly IReadOnlySet<string> Mandatory = new HashSet<string> { WaterAlerts, Orders, TraceabilityQrSent };
}
