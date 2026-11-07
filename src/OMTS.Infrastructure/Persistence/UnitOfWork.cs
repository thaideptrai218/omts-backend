using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Common;
using OMTS.Infrastructure.Messaging;

namespace OMTS.Infrastructure.Persistence;

/// <summary>
/// All repositories of a request share the scoped <see cref="OmtsDbContext"/>, so one save commits
/// their changes together; interceptors (permission cache) run here.
/// </summary>
internal sealed class UnitOfWork(OmtsDbContext db, PushOutbox outbox) : IUnitOfWork
{
    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            throw new ConcurrencyConflictException(exception);
        }
        catch (DbUpdateException exception) when (exception.InnerException is SqlException { Number: 2601 or 2627 })
        {
            // 2601/2627: duplicate key in a unique index or constraint.
            throw new DuplicateRecordException(exception);
        }
        catch (DbUpdateException exception) when (exception.InnerException is SqlException { Number: 547 } sql && IsMissingReference(sql))
        {
            // 547 on an INSERT or UPDATE against a FOREIGN KEY: a row this save points to was removed by a parallel save after it was read (e.g. a harvest lot removed while
            // a sale of it is saved). The caller reads again, as after any other conflict. A DELETE against a key (a row still in use) and a CHECK stay errors.
            throw new ConcurrencyConflictException(exception);
        }
        catch (Exception exception) when (SqlServerDeadlock.IsVictim(exception))
        {
            // The whole save was rolled back because another save got in the way, so the caller reloads and tries again.
            throw new ConcurrencyConflictException(exception);
        }
    }

    public Task ReloadAsync(object entity, CancellationToken cancellationToken = default) =>
        db.Entry(entity).ReloadAsync(cancellationToken);

    public void DiscardChanges()
    {
        db.ChangeTracker.Clear();
        outbox.Discard();
    }

    public void ExpectRowVersion(AuditableEntity entity, byte[] rowVersion) =>
        db.Entry(entity).Property(e => e.RowVersion).OriginalValue = rowVersion;

    public void MarkChanged(AuditableEntity entity)
    {
        var entry = db.Entry(entity);
        if (entry.State == EntityState.Unchanged)
        {
            entry.State = EntityState.Modified;
        }
    }

    // "The INSERT statement conflicted with the FOREIGN KEY constraint …" (or UPDATE): the referenced row is gone. SQL Server words error 547 the same for CHECK constraints.
    private static bool IsMissingReference(SqlException exception) =>
        exception.Message.Contains("FOREIGN KEY", StringComparison.Ordinal)
        && (exception.Message.Contains("INSERT statement", StringComparison.Ordinal) || exception.Message.Contains("UPDATE statement", StringComparison.Ordinal));
}
