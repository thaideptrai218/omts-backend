using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Entities.Identity;

namespace OMTS.Infrastructure.Persistence.Repositories;

/// <summary>Only the rows of the fixed roles: an old role row kept by the FixedRoles migration is never found.</summary>
internal sealed class RoleRepository(OmtsDbContext db) : IRoleRepository
{
    public Task<Role?> GetByIdAsync(Guid roleId, CancellationToken cancellationToken) =>
        db.Roles.AsNoTracking().OfFixedRoles().FirstOrDefaultAsync(r => r.Id == roleId, cancellationToken);

    public Task<Role?> FindByCodeAsync(string code, CancellationToken cancellationToken) =>
        db.Roles.AsNoTracking().OfFixedRoles().FirstOrDefaultAsync(r => r.Code == code, cancellationToken);

    public async Task<IReadOnlyList<Role>> ListAsync(CancellationToken cancellationToken) =>
        await db.Roles.AsNoTracking().OfFixedRoles().OrderBy(r => r.Name).ToListAsync(cancellationToken);
}
