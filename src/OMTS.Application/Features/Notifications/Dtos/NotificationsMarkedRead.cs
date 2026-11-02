using System.Text.Json;

namespace OMTS.Application.Features.Notifications.Dtos;

public sealed record NotificationsMarkedRead(int MarkedRead, int UnreadCount);
