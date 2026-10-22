using Microsoft.Data.SqlClient;
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
        catch (DbUpdateException exception) when (exception.InnerException is SqlException { Number: 2601 or 2627 })
        {
            // 2601/2627: duplicate key in a unique index or constraint.
            throw new DuplicateRecordException(exception);
        }
    }

    public Task ReloadAsync(object entity, CancellationToken cancellationToken = default) =>
        db.Entry(entity).ReloadAsync(cancellationToken);

    public void ExpectRowVersion(AuditableEntity entity, byte[] rowVersion) =>
        db.Entry(entity).Property(e => e.RowVersion).OriginalValue = rowVersion;
}
