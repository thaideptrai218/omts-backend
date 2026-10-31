using OMTS.Application.Common.Models.Messaging;
using OMTS.Application.Common.Interfaces.Messaging;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Notifications;

public sealed class NotificationDispatcher(
    INotificationRepository notifications,
    IPushDeviceRepository devices,
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
        var pushRecipients = new List<Guid>();

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
                    Title = Truncate(title, MaxTitleLength)!,
                    Body = Truncate(body, MaxBodyLength),
                    RelatedRecordType = relatedRecordType,
                    RelatedRecordCode = relatedRecordCode,
                    CreatedAt = now,
                    IsMandatory = mandatory,
                });
            }

            if (mandatory || IsEnabled(accountPreferences, category, NotificationChannel.Push))
            {
                pushRecipients.Add(accountId);
            }
        }

        if (pushRecipients.Count == 0)
        {
            return;
        }

        // One query for every recipient: the devices still logged in with the session that registered them.
        var tokens = (await devices.ListDeliverableAsync(pushRecipients, now, cancellationToken)).ToLookup(d => d.AccountId, d => d.Token);
        foreach (var accountId in pushRecipients)
        {
            pushOutbox.Enqueue(new PushMessage(accountId, type, title, body, relatedRecordType, relatedRecordCode, tokens[accountId].ToList()));
        }
    }

    // Column sizes of Notification: a long text must not fail the caller's business save.
    private const int MaxTitleLength = 200;
    private const int MaxBodyLength = 2000;

    private static string? Truncate(string? value, int maxLength) =>
        value is not null && value.Length > maxLength ? value[..maxLength] : value;

    // A channel without a saved preference is on (ACS-07 defaults).
    private static bool IsEnabled(IReadOnlyList<NotificationPreference> preferences, string category, NotificationChannel channel) =>
        preferences.FirstOrDefault(p => p.Category == category && p.Channel == channel)?.Enabled ?? true;
}
