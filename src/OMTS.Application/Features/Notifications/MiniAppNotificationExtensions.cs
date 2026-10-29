using OMTS.Application.Common.Messages;

namespace OMTS.Application.Features.Notifications;

/// <summary>Adapts the Mini App's existing messages to the shared notification delivery and commit rules.</summary>
public static class MiniAppNotificationExtensions
{
    public static Task NotifyAsync(this INotificationDispatcher dispatcher, IReadOnlyCollection<Guid> recipients,
        string type, string title, string? body, string? recordType, string? recordCode, bool isMandatory, CancellationToken ct, Guid? recordId = null)
    {
        RelatedRecord? record = recordType is null ? null : new(recordType,
            recordId ?? (Guid.TryParse(recordCode, out var id) ? id : Guid.Empty), recordCode);
        return dispatcher.NotifyAsync(recipients, new NotificationContent(type, title, UserMessages.MiniAppNotification(body ?? title), record), ct);
    }
}
