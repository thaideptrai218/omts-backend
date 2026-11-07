using OMTS.Application.Common.Exceptions;
using OMTS.Domain.Common;

namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>
/// Commits everything the repositories of the current request have added or changed, in one
/// transaction. Repositories never save on their own.
/// </summary>
public interface IUnitOfWork
{
    /// <exception cref="ConcurrencyConflictException">A changed record was modified by someone else meanwhile, or the database chose this save as a deadlock victim.</exception>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>Re-reads a tracked record from the database, dropping its unsaved changes (retry after a conflict).</summary>
    Task ReloadAsync(object entity, CancellationToken cancellationToken = default);

    /// <summary>
    /// Forgets every change not saved yet (rows added, records read) and the push and SMS messages queued with them, so a use
    /// case that lost a save conflict can run again from fresh reads without saving or announcing its first attempt too.
    /// </summary>
    void DiscardChanges();

    /// <summary>The save must fail if the record's row version is no longer the one the client edited.</summary>
    void ExpectRowVersion(AuditableEntity entity, byte[] rowVersion);

    /// <summary>Saves the record as changed (new row version, updated by/at) although only related rows changed.</summary>
    void MarkChanged(AuditableEntity entity);
}
