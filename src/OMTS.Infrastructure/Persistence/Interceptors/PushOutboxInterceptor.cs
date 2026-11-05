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
    // The limit of one push.
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(10);

    // How long the answer waits for all the pushes of a save, whatever their number or what a provider does with its cancellation.
    private static readonly TimeSpan PushWait = SendTimeout;

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

    // A row version conflict does not count as a failed save for EF Core: it only comes through here. A use case that
    // retries after one would otherwise announce the attempt that was lost.
    public override InterceptionResult ThrowingConcurrencyException(ConcurrencyExceptionEventData eventData, InterceptionResult result)
    {
        outbox.Discard();
        return result;
    }

    public override ValueTask<InterceptionResult> ThrowingConcurrencyExceptionAsync(
        ConcurrencyExceptionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
    {
        outbox.Discard();
        return ValueTask.FromResult(result);
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

    // Each channel on its own thread: a provider that hangs or blocks its thread must not hold the other channel back.
    // The wait is bounded by the longer of PushWait and the SMS limit (at most 60 seconds, checked at startup); what is
    // left of either channel goes on without the answer.
    private async Task SendPendingAsync()
    {
        var pushes = outbox.TakeAll();
        var sms = outbox.TakeAllSms();
        if (pushes.Count == 0 && sms.Count == 0)
        {
            return;
        }

        var smsSent = Task.Run(() => mandatorySms.SendAsync(sms), CancellationToken.None);
        var pushesSent = Task.Run(() => SendPushAsync(pushes), CancellationToken.None);
        try
        {
            await pushesSent.WaitAsync(PushWait);
        }
        catch (TimeoutException)
        {
            logger.LogWarning("{Count} push messages of a save not all answered after {Seconds} seconds; the save is kept.", pushes.Count, PushWait.TotalSeconds);
        }

        await smsSent;
    }

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
