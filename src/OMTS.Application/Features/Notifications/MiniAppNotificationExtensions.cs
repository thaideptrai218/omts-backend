using OMTS.Application.Common.Messages;

namespace OMTS.Application.Features.Notifications;

/// <summary>Adapts the Mini App's existing messages to the shared notification delivery and commit rules.</summary>
public static class MiniAppNotificationExtensions
{
    /// <param name="recordId">The id of the record the notification opens; required, so a notification never points at no record.</param>
    public static Task NotifyAsync(this INotificationDispatcher dispatcher, IReadOnlyCollection<Guid> recipients,
        string type, string title, string? body, string recordType, string? recordCode, bool isMandatory, CancellationToken ct, Guid recordId)
    {
        RelatedRecord record = new(recordType, recordId, recordCode);
        return dispatcher.NotifyAsync(recipients, new NotificationContent(type, title, UserMessages.MiniAppNotification(body ?? title), record), ct);
    }
}
