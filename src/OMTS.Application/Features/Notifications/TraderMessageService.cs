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
/// caller's answer does not change, whatever the service does (GFR-06). A row left Sending by a server stop stays so: the
/// SRS has no timer and a response has no Resend, so it blocks nothing.
/// </summary>
public sealed class TraderMessageService(
    INotificationRepository notifications,
    ITraderMessageSender sender,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider,
    ILogger<TraderMessageService> logger)
{
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(10);
    private const int MaxErrorCodeLength = 100;
    private const string SendFailed = "SEND_FAILED";

    /// <summary>The row to pass to <see cref="DeliverAsync"/>. <paramref name="recipientAccountId"/> is the account the profile is linked to.</summary>
    public Notification Queue(Guid recipientAccountId, string phoneNumber, NotificationContent content)
    {
        var now = timeProvider.GetUtcNow();
        var row = new Notification
        {
            RecipientAccountId = recipientAccountId,
            Channel = sender.Channel,
            Type = content.Type,
            Title = content.Title,
            Body = content.Message.Text,
            MessageCode = content.Message.Code,
            MessageParams = MessageParameterJson.Serialize(content.Message.Parameters),
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

    /// <summary>Never throws. Sends with its own limit: the data is committed, so the request's cancellation must not leave the row Sending.</summary>
    public async Task DeliverAsync(Notification row)
    {
        var result = await SendAsync(row);
        row.DeliveryStatusId = result.Succeeded ? StatusIds.MessageDelivery.Sent : StatusIds.MessageDelivery.NotSent;
        row.DeliveryStatusAt = timeProvider.GetUtcNow();
        row.ProviderMessageId = result.MessageId;
        row.ProviderErrorCode = result.ErrorCode is { Length: > MaxErrorCodeLength } code ? code[..MaxErrorCodeLength] : result.ErrorCode;
        if (!result.Succeeded)
        {
            // The type and the error code only: never the number or the text.
            logger.LogWarning("Message {Type} to a trader not sent: {Error}", row.Type, row.ProviderErrorCode);
        }

        try
        {
            await unitOfWork.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogError("The delivery status of message {Type} could not be saved: {Exception}", row.Type, exception.GetType().Name);
        }
    }

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
