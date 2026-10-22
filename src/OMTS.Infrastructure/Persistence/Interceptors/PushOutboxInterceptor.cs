using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using OMTS.Application.Common.Interfaces.Messaging;
using OMTS.Infrastructure.Messaging;

namespace OMTS.Infrastructure.Persistence.Interceptors;

/// <summary>
/// Sends the queued push messages once the save has committed. A push failure is logged and never rolls
/// back the business change; a failed save drops the queue so nothing unsaved is announced.
/// </summary>
internal sealed class PushOutboxInterceptor(PushOutbox outbox, IPushSender pushSender, ILogger<PushOutboxInterceptor> logger)
    : SaveChangesInterceptor
{
    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        SendPendingAsync(CancellationToken.None).GetAwaiter().GetResult();
        return result;
    }

    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        await SendPendingAsync(cancellationToken);
        return result;
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData) => outbox.Discard();

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        outbox.Discard();
        return Task.CompletedTask;
    }

    private async Task SendPendingAsync(CancellationToken cancellationToken)
    {
        foreach (var message in outbox.TakeAll())
        {
            try
            {
                var result = await pushSender.SendAsync(message, cancellationToken);
                if (!result.Succeeded)
                {
                    logger.LogWarning(
                        "Push {Type} to account {Recipient} failed: {Error}", message.Type, message.RecipientAccountId, result.ErrorCode);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Push {Type} to account {Recipient} failed", message.Type, message.RecipientAccountId);
            }
        }
    }
}
