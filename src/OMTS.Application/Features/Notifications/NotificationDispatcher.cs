using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Notifications;

/// <summary>Sends the notifications of the modules: kept in the recipients' lists, and by SMS for the mandatory types (formerly API-02).</summary>
public interface INotificationDispatcher
{
    /// <summary>
    /// Adds one <see cref="Notification"/> row per active recipient to the current unit of work (saved with the caller's
    /// change). A type with an SMS rule also queues its SMS, which is sent only after that save commits; a failure there
    /// is logged and never rolls back the change (GFR-06), under the same rule of the commit (<see cref="MandatorySmsQueue"/>).
    /// </summary>
    /// <exception cref="ArgumentException">The type is not registered in <see cref="NotificationTypes.All"/>.</exception>
    Task NotifyAsync(IReadOnlyCollection<Guid> recipientAccountIds, NotificationContent content, CancellationToken cancellationToken);
}

public sealed class NotificationDispatcher(
    INotificationRepository notifications,
    MandatorySmsQueue mandatorySms,
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
        var active = await notifications.GetActiveAccountIdsAsync(recipients, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var parameters = MessageParameterJson.Serialize(content.Message.Parameters);

        foreach (var accountId in recipients.Where(active.Contains))
        {
            notifications.Add(new Notification
            {
                RecipientAccountId = accountId,
                Channel = NotificationChannel.InApp,
                Type = definition.Code,
                Title = Truncate(content.Title, MaxTitleLength)!,
                Body = Truncate(content.Message.Text, MaxBodyLength),
                MessageCode = content.Message.Code,
                MessageParams = parameters is { Length: <= MaxBodyLength } ? parameters : null,
                RelatedRecordType = content.Record?.Type,
                RelatedRecordId = content.Record?.Id,
                RelatedRecordCode = content.Record?.Code,
                CreatedAt = now,
                IsMandatory = definition.IsMandatory,
            });
        }

        await mandatorySms.QueueAsync(definition, content, recipients.Where(active.Contains).ToList(), cancellationToken);
    }

    private static string? Truncate(string? value, int maxLength) =>
        value is not null && value.Length > maxLength ? value[..maxLength] : value;
}
