using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Entities.Identity;

namespace OMTS.Infrastructure.Persistence.Repositories;

internal sealed class EmployeeRepository(OmtsDbContext db) : IEmployeeRepository
{
    public Task<Employee?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        db.Employees.FirstOrDefaultAsync(e => e.Id == id, cancellationToken);

    public Task<Employee?> GetByAccountIdAsync(Guid accountId, CancellationToken cancellationToken) =>
        db.Employees.FirstOrDefaultAsync(e => e.AccountId == accountId, cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, Guid?>> GetIdsByAccountIdsAsync(IReadOnlyCollection<Guid> accountIds, CancellationToken cancellationToken) =>
        await db.Employees.AsNoTracking()
            .Where(e => e.AccountId != null && accountIds.Contains(e.AccountId.Value))
            .ToDictionaryAsync(e => e.AccountId!.Value, e => (Guid?)e.Id, cancellationToken);
}
