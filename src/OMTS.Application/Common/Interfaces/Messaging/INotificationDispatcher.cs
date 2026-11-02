using OMTS.Application.Features.Notifications;
using OMTS.Application.Common.Messages;
namespace OMTS.Application.Common.Interfaces.Messaging;

public interface INotificationDispatcher
{
    Task NotifyAsync(IReadOnlyCollection<Guid> recipientAccountIds, NotificationContent content, CancellationToken cancellationToken);

    // Compatibility for the existing Zalo/sales/workforce producers. They retain their original text until
    // their module has an SRS message code; record codes are never treated as record IDs unless they are UUIDs.
    Task NotifyAsync(IReadOnlyCollection<Guid> recipientAccountIds, string type, string title, string? body,
        string? relatedRecordType, string? relatedRecordCode, bool isMandatory, CancellationToken cancellationToken) =>
        NotifyAsync(recipientAccountIds, new NotificationContent(type, title, UserMessages.LegacyNotification(body),
            relatedRecordType is not null && Guid.TryParse(relatedRecordCode, out var id)
                ? new RelatedRecord(relatedRecordType, id, relatedRecordCode) : null)
            { LegacyRecordType = relatedRecordType, LegacyRecordCode = relatedRecordCode }, cancellationToken);
}
