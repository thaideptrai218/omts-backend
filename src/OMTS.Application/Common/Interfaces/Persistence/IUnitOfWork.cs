namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>
/// Commits everything the repositories of the current request have added or changed, in one
/// transaction. Repositories never save on their own.
/// </summary>
public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
