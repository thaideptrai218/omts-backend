using OMTS.Application.Common.Interfaces.Persistence;

namespace OMTS.Infrastructure.Persistence;

/// <summary>
/// All repositories of a request share the scoped <see cref="OmtsDbContext"/>, so one save commits
/// their changes together; interceptors (permission cache) run here.
/// </summary>
internal sealed class UnitOfWork(OmtsDbContext db) : IUnitOfWork
{
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => db.SaveChangesAsync(cancellationToken);
}
