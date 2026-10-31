namespace OMTS.Application.Features.Notifications.Dtos;

/// <summary>A notification of the caller's list (SCR-ACCOUNT-07), with the content it was sent with (BR-ACCOUNT-12).</summary>
/// <param name="Title">English title.</param>
/// <param name="Body">English text of <paramref name="MessageCode"/>.</param>
/// <param name="MessageCode">SRS code of the content (MSG-WATER-03…), to show it in the client's language.</param>
/// <param name="MessageParams">Values of the placeholders of <paramref name="MessageCode"/>, by placeholder name.</param>
/// <param name="Mandatory">The type is always pushed.</param>
/// <param name="ReadAt">Null while unread.</param>
public sealed record NotificationView(
    Guid Id,
    string Type,
    string Title,
    string? Body,
    string? MessageCode,
    IReadOnlyDictionary<string, string>? MessageParams,
    bool Mandatory,
    string? RelatedRecordType,
    Guid? RelatedRecordId,
    string? RelatedRecordCode,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReadAt);

/// <summary>One page of the list, newest first, and the number of unread notifications of the whole list.</summary>
public sealed record NotificationPage(IReadOnlyList<NotificationView> Items, int Page, int PageSize, int Total, int UnreadCount);

/// <param name="MarkedRead">How many notifications were unread and are now read.</param>
public sealed record NotificationsMarkedRead(int MarkedRead, int UnreadCount);

/// <summary>The related record the client opens in the use case that manages it, after the access check (ACCOUNT-08 step 5).</summary>
/// <param name="ZoneId">The zone of the record when it has one (e.g. to open WATER-01 with the zone chosen).</param>
public sealed record OpenedNotificationRecord(Guid NotificationId, string RecordType, Guid RecordId, string? RecordCode, Guid? ZoneId);
