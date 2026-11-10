using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using OMTS.Application.Common.Interfaces.Identity;
using OMTS.Domain.Entities.Identity;
using OMTS.Infrastructure.Identity.Revocation;

namespace OMTS.Infrastructure.Persistence.Interceptors;

/// <summary>
/// Publishes every session revoked by a save to the revocation store once the save has committed, so each writer
/// (logout, password change, lock, refresh reuse, expiry of a refresh) is denied on the next request without having to
/// remember it. Revocations are tracked entity changes on purpose: a bulk update would bypass this interceptor.
/// The store write never throws and never fails the save; when it fails, the sweeper is woken to copy the revocation
/// from the database, which already holds it.
/// </summary>
public sealed class SessionRevocationInterceptor(
    ISessionRevocationStore store,
    RevocationSweepSignal sweepSignal,
    ILogger<SessionRevocationInterceptor> logger) : SaveChangesInterceptor
{
    // Captured before saving and applied after the commit; a failed save discards them.
    private readonly ConditionalWeakTable<DbContext, List<RevokedSession>> _pending = new();

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Capture(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Capture(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        // A synchronous save must not wait for Redis: the sweeper publishes it right away instead.
        if (Take(eventData.Context) is not null)
        {
            sweepSignal.Request();
        }

        return result;
    }

    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        if (Take(eventData.Context) is { } revoked)
        {
            // Not the request's cancellation token: the save is committed and the entry must still be written
            // if the client has gone. The store bounds every command with its own short timeout.
            var written = false;
            try
            {
                written = await store.TryMarkRevokedAsync(revoked);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning("Publishing revoked sessions failed ({ExceptionType}).", exception.GetType().Name);
            }

            if (!written)
            {
                sweepSignal.Request();
            }
        }

        return result;
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData) => Discard(eventData.Context);

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Discard(eventData.Context);
        return Task.CompletedTask;
    }

    private void Capture(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        List<RevokedSession>? revoked = null;
        foreach (var entry in context.ChangeTracker.Entries<LoginSession>())
        {
            if (entry.State == EntityState.Added && entry.Entity.RevokedAt is { } addedRevokedAt)
            {
                (revoked ??= []).Add(new RevokedSession(entry.Entity.Id, addedRevokedAt));
            }
            else if (entry.State == EntityState.Modified
                && entry.Property(s => s.RevokedAt) is { IsModified: true, OriginalValue: null, CurrentValue: { } revokedAt })
            {
                (revoked ??= []).Add(new RevokedSession(entry.Entity.Id, revokedAt));
            }
        }

        if (revoked is null)
        {
            _pending.Remove(context);
        }
        else
        {
            _pending.AddOrUpdate(context, revoked);
        }
    }

    private List<RevokedSession>? Take(DbContext? context)
    {
        if (context is null || !_pending.TryGetValue(context, out var revoked))
        {
            return null;
        }

        _pending.Remove(context);
        return revoked;
    }

    private void Discard(DbContext? context)
    {
        if (context is not null)
        {
            _pending.Remove(context);
        }
    }
}
