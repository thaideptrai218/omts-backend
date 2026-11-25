using Microsoft.EntityFrameworkCore.Diagnostics;
using OMTS.Infrastructure.Messaging;

namespace OMTS.Infrastructure.Persistence.Interceptors;

/// <summary>
/// Sends the queued mandatory SMS once the save has committed. A failure is logged and never rolls
/// back the business change; a failed or cancelled save drops the queue so nothing unsaved is announced. A row version
/// conflict is not a failed save for EF Core: the queue stays with the rows the tracker still holds, so a retry by reload
/// announces what it commits, and <see cref="OMTS.Application.Common.Interfaces.Persistence.IUnitOfWork.DiscardChanges"/> drops both.
/// </summary>
internal sealed class MandatorySmsOutboxInterceptor(MandatorySmsOutbox outbox, MandatorySmsDelivery mandatorySms) : SaveChangesInterceptor
{
    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        SendPendingAsync().GetAwaiter().GetResult();
        return result;
    }

    // The data is committed at this point: the request's cancellation must neither fail the save nor drop
    // the remaining messages, so sends use their own timeout instead.
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

    // On its own thread: a provider that hangs or blocks its thread must not hold the save's caller on a synchronization context.
    // The wait is bounded by the SMS limit (at most 60 seconds, checked at startup).
    private async Task SendPendingAsync()
    {
        var sms = outbox.TakeAllSms();
        if (sms.Count == 0)
        {
            return;
        }

        await Task.Run(() => mandatorySms.SendAsync(sms), CancellationToken.None);
    }
}
