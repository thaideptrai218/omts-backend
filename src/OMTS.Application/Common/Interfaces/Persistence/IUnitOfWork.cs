using OMTS.Application.Common.Exceptions;

namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>
/// Commits everything the repositories of the current request have added or changed, in one
/// transaction. Repositories never save on their own.
/// </summary>
public interface IUnitOfWork
{
    /// <exception cref="ConcurrencyConflictException">A changed record was modified by someone else meanwhile.</exception>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>Re-reads a tracked record from the database, dropping its unsaved changes (retry after a conflict).</summary>
    Task ReloadAsync(object entity, CancellationToken cancellationToken = default);
}
