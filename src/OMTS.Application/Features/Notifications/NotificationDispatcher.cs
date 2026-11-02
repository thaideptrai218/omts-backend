using OMTS.Application.Common.Models.Messaging;
using OMTS.Application.Common.Interfaces.Messaging;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
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
    // Column sizes of Notification: a long text must not fail the caller's business save.
    private const int MaxTitleLength = 200;
    private const int MaxBodyLength = 2000;

    public async Task NotifyAsync(IReadOnlyCollection<Guid> recipientAccountIds, NotificationContent content, CancellationToken cancellationToken)
    {
        var definition = NotificationTypes.Find(content.Type)
            ?? throw new ArgumentException($"Notification type '{content.Type}' is not registered in NotificationTypes.All.", nameof(content));
        ArgumentException.ThrowIfNullOrWhiteSpace(content.Title);

        var recipients = recipientAccountIds.Distinct().ToList();
        if (recipients.Count == 0)
        {
            return;
        }

        // Only active accounts are notified; Locked accounts are left out by the repository.
        var preferences = await notifications.GetPreferencesAsync(recipients, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var parameters = MessageParameterJson.Serialize(content.Message.Parameters);
        var pushed = new List<Notification>();

        foreach (var accountId in recipients)
        {
            if (!preferences.TryGetValue(accountId, out var accountPreferences))
            {
                continue;
            }

            var notification = new Notification
            {
                RecipientAccountId = accountId,
                Channel = NotificationChannel.InApp,
                Type = definition.Code,
                Title = Truncate(content.Title, MaxTitleLength)!,
                Body = Truncate(content.Message.Text, MaxBodyLength),
                MessageCode = string.IsNullOrEmpty(content.Message.Code) ? null : content.Message.Code,
                MessageParams = parameters is { Length: <= MaxBodyLength } ? parameters : null,
                RelatedRecordType = content.Record?.Type ?? content.LegacyRecordType,
                RelatedRecordId = content.Record?.Id,
                RelatedRecordCode = content.Record?.Code ?? content.LegacyRecordCode,
                CreatedAt = now,
                IsMandatory = definition.IsMandatory,
            };
            notifications.Add(notification);

            if (IsPushed(definition, accountPreferences))
            {
                pushed.Add(notification);
            }
        }

        if (pushed.Count == 0)
        {
            return;
        }

        // One query for every recipient: the devices still logged in with the session that registered them.
        var tokens = (await devices.ListDeliverableAsync(pushed.Select(n => n.RecipientAccountId).ToList(), now, cancellationToken))
            .ToLookup(d => d.AccountId, d => d.Token);
        foreach (var notification in pushed)
        {
            pushOutbox.Enqueue(new PushMessage(
                notification.RecipientAccountId,
                notification.Type,
                content.Title,
                content.Message.Text,
                notification.RelatedRecordType,
                notification.RelatedRecordCode,
                tokens[notification.RecipientAccountId].ToList(),
                notification.Id,
                notification.MessageCode,
                notification.RelatedRecordId,
                notification.MessageParams));
        }
    }

    /// <summary>Whether a notification of the type is pushed to an account with these preferences.</summary>
    public static bool IsPushed(NotificationTypeDefinition definition, IReadOnlyList<NotificationPreference> preferences) => definition.PushPolicy switch
    {
        NotificationPushPolicy.AlwaysPush => true,
        NotificationPushPolicy.NeverPush => false,
        // A type without a saved choice is pushed (ACCOUNT-07 default).
        _ => preferences.FirstOrDefault(p => p.Type == definition.Code)?.Push ?? true,
    };

    private static string? Truncate(string? value, int maxLength) =>
        value is not null && value.Length > maxLength ? value[..maxLength] : value;
}
