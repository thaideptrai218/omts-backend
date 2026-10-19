using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using OMTS.Application.Common.Interfaces;
using OMTS.Domain.Entities.Identity;

namespace OMTS.Infrastructure.Persistence.Interceptors;

/// <summary>
/// Clears cached permissions whenever grants, roles, role permissions or account state are saved, so
/// every writer (UC11, UC12, UC04, UC08, jobs) makes changes effective immediately (BR-19) without
/// having to remember to invalidate.
/// </summary>
public sealed class PermissionCacheInvalidationInterceptor(IPermissionCacheInvalidator invalidator) : SaveChangesInterceptor
{
    // Changes are captured before saving (deleted entries are detached afterwards) and applied after
    // the commit, so a concurrent request cannot re-cache the old state in between.
    private readonly ConditionalWeakTable<DbContext, PendingInvalidation> _pending = new();

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
        Apply(eventData.Context);
        return result;
    }

    public override ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        Apply(eventData.Context);
        return ValueTask.FromResult(result);
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

        var pending = new PendingInvalidation();
        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
            {
                continue;
            }

            switch (entry.Entity)
            {
                case Role or RolePermission:
                    pending.All = true;
                    break;
                case AccessGrant grant:
                    pending.AccountIds.Add(grant.AccountId);
                    break;
                case Account account:
                    pending.AccountIds.Add(account.Id);
                    break;
            }
        }

        if (pending.All || pending.AccountIds.Count > 0)
        {
            _pending.AddOrUpdate(context, pending);
        }
    }

    private void Apply(DbContext? context)
    {
        if (context is null || !_pending.TryGetValue(context, out var pending))
        {
            return;
        }

        _pending.Remove(context);
        if (pending.All)
        {
            invalidator.InvalidateAll();
            return;
        }

        foreach (var accountId in pending.AccountIds)
        {
            invalidator.InvalidateAccount(accountId);
        }
    }

    private void Discard(DbContext? context)
    {
        if (context is not null)
        {
            _pending.Remove(context);
        }
    }

    private sealed class PendingInvalidation
    {
        public bool All { get; set; }
        public HashSet<Guid> AccountIds { get; } = [];
    }
}
