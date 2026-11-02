using System.Text.Json;

namespace OMTS.Application.Features.Notifications.Dtos;

public sealed record NotificationPage(IReadOnlyList<NotificationView> Items, int Page, int PageSize, int Total, int UnreadCount);
