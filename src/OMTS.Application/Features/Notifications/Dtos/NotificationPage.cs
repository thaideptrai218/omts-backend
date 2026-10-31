namespace OMTS.Application.Features.Notifications.Dtos;

/// <summary>A page of the caller's notifications, newest first, with the unread count for the badge.</summary>
public sealed record NotificationPage(IReadOnlyList<NotificationView> Items, int Total, int UnreadCount);
