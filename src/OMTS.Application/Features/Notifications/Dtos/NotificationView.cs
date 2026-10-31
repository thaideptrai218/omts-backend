namespace OMTS.Application.Features.Notifications.Dtos;

/// <summary>One in-app notification of the caller; unread while <see cref="ReadAt"/> is empty.</summary>
public sealed record NotificationView(
    Guid Id,
    string Type,
    string Title,
    string? Body,
    string? RelatedRecordType,
    string? RelatedRecordCode,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReadAt,
    bool IsMandatory);
