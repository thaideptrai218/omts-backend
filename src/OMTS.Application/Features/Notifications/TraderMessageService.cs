using Microsoft.Extensions.Logging;
using OMTS.Application.Common.Interfaces.Messaging;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Models;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Platform;

namespace OMTS.Application.Features.Notifications;

/// <summary>
/// A message to a trader's phone, kept as a Notification row with its channel and delivery status (BR-SALE-09), never in a
/// list. <see cref="Queue"/> adds the row as Sending to the caller's save; <see cref="DeliverAsync"/>, after that save
/// commits, hands the message to the service and saves Sent or Not sent. Nothing that was saved is undone, and the
/// caller's answer does not change, whatever the service does (GFR-06). A row left Sending by a server stop stays so in
/// the database (the SRS has no timer); <see cref="EffectiveStatus"/> reads it as Not sent once it is older than any send
/// can take, so it never blocks the Resend of an invoice (BR-SALE-09).
/// </summary>
public sealed class TraderMessageService(
    INotificationRepository notifications,
    ITraderMessageSender sender,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider,
    ILogger<TraderMessageService> logger)
{
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(10);

    // Column size of both provider fields of Notification: what a service answers must not fail the second save and leave the row Sending.
    private const int MaxProviderTextLength = 100;

    // Column sizes of the title, the text and the parameters of Notification, as NotificationDispatcher cuts its list rows: a long message (a trader on many invoices,
    // MSG-TRACE-05) must not make the caller's save fail.
    private const int MaxTitleLength = 200;
    private const int MaxBodyLength = 2000;
    private const string SendFailed = "SEND_FAILED";

    /// <summary>A Sending row older than this was left by a stop between the save and the answer of the service: well past <see cref="SendTimeout"/>.</summary>
    public static readonly TimeSpan StaleSendingAfter = TimeSpan.FromMinutes(2);

    /// <summary>The delivery status as shown and as the Resend guard reads it: a stale Sending row is Not sent (it was never answered).</summary>
    public static int EffectiveStatus(int? deliveryStatusId, DateTimeOffset? deliveryStatusAt, DateTimeOffset now) =>
        deliveryStatusId == StatusIds.MessageDelivery.Sending && deliveryStatusAt < now - StaleSendingAfter
            ? StatusIds.MessageDelivery.NotSent
            : deliveryStatusId ?? StatusIds.MessageDelivery.NotSent;

    /// <summary>
    /// The row to pass to <see cref="DeliverAsync"/>. <paramref name="recipientAccountId"/> is the account the profile is linked to, or null for a profile
    /// without a trader account (an invoice, BR-SALE-09).
    /// </summary>
    public Notification Queue(Guid? recipientAccountId, string phoneNumber, NotificationContent content)
    {
        var now = timeProvider.GetUtcNow();
        var parameters = MessageParameterJson.Serialize(content.Message.Parameters);
        var row = new Notification
        {
            RecipientAccountId = recipientAccountId,
            Channel = sender.Channel,
            Type = content.Type,
            Title = Cut(content.Title, MaxTitleLength)!,
            Body = Cut(content.Message.Text, MaxBodyLength),
            MessageCode = content.Message.Code,
            // Cut JSON is no JSON: parameters too long for the column are left out, as the dispatcher does; the text keeps the message.
            MessageParams = parameters is { Length: <= MaxBodyLength } ? parameters : null,
            RelatedRecordType = content.Record?.Type,
            RelatedRecordId = content.Record?.Id,
            RelatedRecordCode = content.Record?.Code,
            CreatedAt = now,
            PhoneNumber = phoneNumber,
            DeliveryStatusId = StatusIds.MessageDelivery.Sending,
            DeliveryStatusAt = now,
        };
        notifications.Add(row);
        return row;
    }

    /// <summary>
    /// A sending that could not be handed to any service (no phone number), added to the caller's save already Not sent with <paramref name="cause"/> as its error code, so
    /// the latest sending of the record says Not sent (BR-SALE-09). It has no phone and is never delivered; like every row of this service it is in no list.
    /// </summary>
    public Notification NotSent(Guid? recipientAccountId, NotificationContent content, string cause)
    {
        var row = Queue(recipientAccountId, string.Empty, content);
        row.PhoneNumber = null;
        row.DeliveryStatusId = StatusIds.MessageDelivery.NotSent;
        row.ProviderErrorCode = FitColumn(cause);
        return row;
    }

    /// <summary>Never throws. Sends with its own limit: the data is committed, so the request's cancellation must not leave the row Sending.</summary>
    public Task DeliverAsync(Notification row) => DeliverAllAsync([row]);

    /// <summary>
    /// <see cref="DeliverAsync"/> for several rows of one save (one per trader, TRACE-04): the messages are handed to the service at the same time, each with its own limit, so the
    /// caller waits about one send, not one per row. Only the sends run in parallel and they touch no unit of work (the sender is a singleton, as for the mandatory SMS); the
    /// statuses are set and saved once, on the caller's unit of work, after every send has answered. Never throws.
    /// </summary>
    public async Task DeliverAllAsync(IReadOnlyList<Notification> rows)
    {
        if (rows.Count == 0)
        {
            return;
        }

        var results = await Task.WhenAll(rows.Select(SendAsync));
        for (var i = 0; i < rows.Count; i++)
        {
            Apply(rows[i], results[i]);
        }

        try
        {
            await unitOfWork.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogError("The delivery status of message {Type} could not be saved: {Exception}", rows[0].Type, exception.GetType().Name);
        }
    }

    private void Apply(Notification row, DeliveryResult result)
    {
        row.DeliveryStatusId = result.Succeeded ? StatusIds.MessageDelivery.Sent : StatusIds.MessageDelivery.NotSent;
        row.DeliveryStatusAt = timeProvider.GetUtcNow();
        row.ProviderMessageId = FitColumn(result.MessageId);
        row.ProviderErrorCode = FitColumn(result.ErrorCode);
        if (!result.Succeeded)
        {
            // The type and the error code only: never the number or the text.
            logger.LogWarning("Message {Type} to a trader not sent: {Error}", row.Type, row.ProviderErrorCode);
        }
    }

    private static string? FitColumn(string? text) => Cut(text, MaxProviderTextLength);

    private static string? Cut(string? text, int maxLength) => text is not null && text.Length > maxLength ? text[..maxLength] : text;

    // On its own task and with a limit it enforces itself: a provider that hangs or ignores its cancellation holds the answer no longer.
    private async Task<DeliveryResult> SendAsync(Notification row)
    {
        try
        {
            using var timeout = new CancellationTokenSource(SendTimeout);
            return await Task.Run(() => sender.SendAsync(row.PhoneNumber!, row.Body!, timeout.Token)).WaitAsync(SendTimeout);
        }
        catch (Exception exception)
        {
            logger.LogError("Message {Type} to a trader failed: {Exception}", row.Type, exception.GetType().Name);
            return DeliveryResult.Failed(SendFailed);
        }
    }
}
