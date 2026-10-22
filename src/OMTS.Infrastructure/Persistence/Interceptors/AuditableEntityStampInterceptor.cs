using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using OMTS.Application.Common.Interfaces;
using OMTS.Domain.Common;
using OMTS.Domain.Entities.Identity;

namespace OMTS.Infrastructure.Persistence.Interceptors;

/// <summary>
/// Stamps who created/changed an <see cref="AuditableEntity"/> and when, on every save, so no handler has
/// to remember it. The account is empty for system actions (bootstrap command, background jobs).
/// </summary>
internal sealed class AuditableEntityStampInterceptor(ICurrentUser currentUser, TimeProvider timeProvider) : SaveChangesInterceptor
{
    // Login bookkeeping is not an edit of the account: it must not replace "last updated by" (an
    // anonymous login would erase who last edited the account).
    private static readonly HashSet<string> AccountLoginTracking =
        [nameof(Account.FailedLoginCount), nameof(Account.LockoutEndAt), nameof(Account.LastLoginAt)];

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Stamp(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Stamp(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private void Stamp(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var now = timeProvider.GetUtcNow();
        var accountId = currentUser.AccountId;
        foreach (var entry in context.ChangeTracker.Entries<AuditableEntity>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    entry.Entity.CreatedAt = now;
                    entry.Entity.CreatedBy ??= accountId;
                    break;
                case EntityState.Modified when entry.Entity is Account && OnlyLoginTrackingChanged(entry):
                    break;
                case EntityState.Modified:
                    entry.Entity.UpdatedAt = now;
                    entry.Entity.UpdatedBy = accountId;
                    // Creation stamps never change after the first save.
                    entry.Property(e => e.CreatedAt).IsModified = false;
                    entry.Property(e => e.CreatedBy).IsModified = false;
                    break;
            }
        }
    }

    private static bool OnlyLoginTrackingChanged(EntityEntry<AuditableEntity> entry) =>
        entry.Properties.Where(p => p.IsModified).All(p => AccountLoginTracking.Contains(p.Metadata.Name));
}
