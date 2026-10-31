using OMTS.Domain.Common;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;

namespace OMTS.Domain.Entities.Platform;

/// <summary>
/// E46 – a notification kept in the list of its recipient account (BR-ACCOUNT-12), with the time it was read; unread
/// while <see cref="ReadAt"/> is empty. The same entity keeps each SMS or Zalo OA sending to a trader with its delivery
/// status (<see cref="Channel"/> Sms or ZaloOa, BR-SALE-09); those rows are never in a list. The related record is a
/// type, id and code, not a foreign key: opening it re-checks the access of the user (GFR-02).
/// </summary>
public class Notification : Entity
{
    public Guid RecipientAccountId { get; set; }
    public Account RecipientAccount { get; set; } = null!;

    public NotificationChannel Channel { get; set; } = NotificationChannel.InApp;

    public string Type { get; set; } = string.Empty;

    /// <summary>English title and content as sent; a notification keeps the content it was sent with (BR-ACCOUNT-12).</summary>
    public string Title { get; set; } = string.Empty;
    public string? Body { get; set; }

    /// <summary>SRS message code of <see cref="Body"/> (MSG-WATER-03…), so clients can show the text in their language.</summary>
    public string? MessageCode { get; set; }

    /// <summary>Values of the placeholders of <see cref="MessageCode"/> as a JSON object, e.g. {"zone_name":"Vùng A"}.</summary>
    public string? MessageParams { get; set; }

    public string? RelatedRecordType { get; set; }
    public Guid? RelatedRecordId { get; set; }

    /// <summary>The code shown for the record when it was sent (zone code, license number…); empty for records without a code.</summary>
    public string? RelatedRecordCode { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReadAt { get; set; }

    /// <summary>The type is always pushed and its push cannot be turned off (BR-ACCOUNT-11).</summary>
    public bool IsMandatory { get; set; }

    /// <summary>SMS / Zalo OA rows: the phone number the message was sent to.</summary>
    public string? PhoneNumber { get; set; }

    /// <summary>SMS / Zalo OA rows: Sending, Sent or Not sent (status group MESSAGE_DELIVERY); empty for list rows.</summary>
    public int? DeliveryStatusId { get; set; }
    public Status? DeliveryStatus { get; set; }

    /// <summary>When <see cref="DeliveryStatusId"/> was set (handed to the service, accepted or not accepted).</summary>
    public DateTimeOffset? DeliveryStatusAt { get; set; }

    /// <summary>Id the SMS Service or Zalo OA gave the accepted message.</summary>
    public string? ProviderMessageId { get; set; }

    /// <summary>Error code of the service that did not accept the message (GFR-06).</summary>
    public string? ProviderErrorCode { get; set; }
}
