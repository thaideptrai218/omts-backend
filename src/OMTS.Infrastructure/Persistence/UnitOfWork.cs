using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Common;

namespace OMTS.Infrastructure.Persistence;

/// <summary>
/// All repositories of a request share the scoped <see cref="OmtsDbContext"/>, so one save commits
/// their changes together; interceptors (permission cache) run here.
/// </summary>
internal sealed class UnitOfWork(OmtsDbContext db) : IUnitOfWork
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
    }

    public Task ReloadAsync(object entity, CancellationToken cancellationToken = default) =>
        db.Entry(entity).ReloadAsync(cancellationToken);

    public void ExpectRowVersion(AuditableEntity entity, byte[] rowVersion) =>
        db.Entry(entity).Property(e => e.RowVersion).OriginalValue = rowVersion;
}
