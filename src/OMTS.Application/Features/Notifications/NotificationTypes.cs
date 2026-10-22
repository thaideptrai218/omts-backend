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

/// <summary>Notification types written by the modules, each in one preference category.</summary>
public static class NotificationTypes
{
    public const string WaterAlert = "WATER_ALERT";
    public const string WaterMeasurementOverdue = "WATER_MEASUREMENT_OVERDUE";
    public const string ZoneLicenseExpiry = "ZONE_LICENSE_EXPIRY";
    public const string WorkAssignment = "WORK_ASSIGNMENT";
    public const string PayrollSentBack = "PAYROLL_SENT_BACK";
    public const string NewOrder = "NEW_ORDER";
    public const string TraceQrSent = "TRACE_QR_SENT";

    private static readonly Dictionary<string, string> CategoryByType = new()
    {
        [WaterAlert] = NotificationCategories.WaterAlerts,
        [WaterMeasurementOverdue] = NotificationCategories.DeadlineReminders,
        [ZoneLicenseExpiry] = NotificationCategories.DeadlineReminders,
        [WorkAssignment] = NotificationCategories.Work,
        [PayrollSentBack] = NotificationCategories.Work,
        [NewOrder] = NotificationCategories.Orders,
        [TraceQrSent] = NotificationCategories.TraceabilityQrSent,
    };

    /// <summary>Preference category of a type; an unknown type is its own category.</summary>
    public static string CategoryOf(string type) => CategoryByType.GetValueOrDefault(type, type);
}
