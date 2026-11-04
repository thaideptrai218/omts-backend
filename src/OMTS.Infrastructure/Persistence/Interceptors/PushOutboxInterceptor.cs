using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using OMTS.Application.Common.Interfaces.Messaging;
using OMTS.Infrastructure.Messaging;

namespace OMTS.Infrastructure.Persistence.Interceptors;

/// <summary>
/// Sends the queued push messages and mandatory SMS once the save has committed. A failure is logged and never rolls
/// back the business change; a failed or cancelled save drops the queue so nothing unsaved is announced.
/// </summary>
internal sealed class PushOutboxInterceptor(
    PushOutbox outbox, IPushSender pushSender, MandatorySmsDelivery mandatorySms, ILogger<PushOutboxInterceptor> logger)
    : SaveChangesInterceptor
{
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(10);

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        SendPendingAsync().GetAwaiter().GetResult();
        return result;
    }

    // The data is committed at this point: the request's cancellation must neither fail the save nor drop
    // the remaining pushes, so sends use their own timeout instead.
    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        await SendPendingAsync();
        return result;
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData) => outbox.Discard();

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        outbox.Discard();
        return Task.CompletedTask;
    }

    public override void SaveChangesCanceled(DbContextEventData eventData) => outbox.Discard();

    public override Task SaveChangesCanceledAsync(DbContextEventData eventData, CancellationToken cancellationToken = default)
    {
        outbox.Discard();
        return Task.CompletedTask;
    }

    // Together: a provider of one channel that hangs must not hold the other channel back.
    private Task SendPendingAsync() => Task.WhenAll(SendPushAsync(outbox.TakeAll()), mandatorySms.SendAsync(outbox.TakeAllSms()));

    private async Task SendPushAsync(IReadOnlyList<PushMessage> messages)
    {
        foreach (var message in messages)
        {
            try
            {
                using var timeout = new CancellationTokenSource(SendTimeout);
                var result = await pushSender.SendAsync(message, timeout.Token);
                if (!result.Succeeded)
                {
                    logger.LogWarning(
                        "Push {Type} to account {Recipient} failed: {Error}", message.Type, message.RecipientAccountId, result.ErrorCode);
                }
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Push {Type} to account {Recipient} failed", message.Type, message.RecipientAccountId);
            }
        }
    }
}
