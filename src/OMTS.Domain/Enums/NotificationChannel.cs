namespace OMTS.Domain.Enums;

/// <summary>
/// How a <see cref="Entities.Platform.Notification"/> row reached its recipient (SRS 27/10 Entity Notification).
/// Only <see cref="InApp"/> rows are notifications of the list (ACCOUNT-08), pushed or not; SMS and Zalo OA rows
/// keep the sendings to a trader with their delivery status (BR-SALE-09) and never appear in a list.
/// </summary>
public enum NotificationChannel
{
    /// <summary>A notification of the recipient account's list, also pushed when its type says so (BR-ACCOUNT-11).</summary>
    InApp,

    Sms,

    ZaloOa,
}
