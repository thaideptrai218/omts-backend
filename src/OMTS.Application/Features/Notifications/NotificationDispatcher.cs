using OMTS.Application.Common.Interfaces.Messaging;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Notifications;

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

public sealed class NotificationDispatcher(
    INotificationRepository notifications,
    IPushOutbox pushOutbox,
    TimeProvider timeProvider) : INotificationDispatcher
{
    public async Task NotifyAsync(
        IReadOnlyCollection<Guid> recipientAccountIds,
        string type,
        string title,
        string? body,
        string? relatedRecordType,
        string? relatedRecordCode,
        bool isMandatory,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        var recipients = recipientAccountIds.Distinct().ToList();
        if (recipients.Count == 0)
        {
            return;
        }

        var category = NotificationTypes.CategoryOf(type);
        var mandatory = isMandatory || NotificationCategories.Mandatory.Contains(category);
        var preferences = await notifications.GetPreferencesAsync(recipients, cancellationToken);
        var now = timeProvider.GetUtcNow();

        foreach (var accountId in recipients)
        {
            if (!preferences.TryGetValue(accountId, out var accountPreferences))
            {
                continue;
            }

            if (mandatory || IsEnabled(accountPreferences, category, NotificationChannel.InApp))
            {
                notifications.Add(new Notification
                {
                    RecipientAccountId = accountId,
                    Type = type,
                    Title = title,
                    Body = body,
                    RelatedRecordType = relatedRecordType,
                    RelatedRecordCode = relatedRecordCode,
                    CreatedAt = now,
                    IsMandatory = mandatory,
                });
            }

            if (mandatory || IsEnabled(accountPreferences, category, NotificationChannel.Push))
            {
                pushOutbox.Enqueue(new PushMessage(accountId, type, title, body, relatedRecordType, relatedRecordCode));
            }
        }
    }

    // A channel without a saved preference is on (ACS-07 defaults).
    private static bool IsEnabled(IReadOnlyList<NotificationPreference> preferences, string category, NotificationChannel channel) =>
        preferences.FirstOrDefault(p => p.Category == category && p.Channel == channel)?.Enabled ?? true;
}
