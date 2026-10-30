using OMTS.Application.Common.Interfaces.Messaging;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Enums;

namespace OMTS.Application.Common.Interfaces.Messaging;

/// <summary>Sends in-app and push notifications for the modules (formerly API-02).</summary>
public interface INotificationDispatcher
{
    /// <summary>
    /// Adds the in-app <see cref="Notification"/> rows to the current unit of work (saved with the caller's
    /// change) and queues push messages that are sent only after that save commits; a push failure is
    /// logged and never rolls back the change. Non-mandatory types follow each account's preferences per
    /// category and channel; mandatory ones are always delivered in-app and by push.
    /// </summary>
    Task NotifyAsync(
        IReadOnlyCollection<Guid> recipientAccountIds,
        string type,
        string title,
        string? body,
        string? relatedRecordType,
        string? relatedRecordCode,
        bool isMandatory,
        CancellationToken cancellationToken);
}
