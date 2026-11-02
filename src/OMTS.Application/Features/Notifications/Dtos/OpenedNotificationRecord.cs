using System.Text.Json;

namespace OMTS.Application.Features.Notifications.Dtos;

public sealed record OpenedNotificationRecord(Guid NotificationId, string RecordType, Guid RecordId, string? RecordCode, Guid? ZoneId);
