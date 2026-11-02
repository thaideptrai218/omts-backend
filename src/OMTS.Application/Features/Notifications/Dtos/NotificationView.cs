using System.Text.Json;

namespace OMTS.Application.Features.Notifications.Dtos;

public sealed record NotificationView(
    Guid Id,
    string Type,
    string Title,
    string? Body,
    string? MessageCode,
    JsonElement? MessageParams,
    bool Mandatory,
    string? RelatedRecordType,
    Guid? RelatedRecordId,
    string? RelatedRecordCode,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReadAt);
