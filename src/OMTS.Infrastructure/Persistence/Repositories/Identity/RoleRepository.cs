using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Entities.Identity;

namespace OMTS.Infrastructure.Persistence.Repositories.Identity;

internal sealed class RoleRepository(OmtsDbContext db) : IRoleRepository
{
    public Task<Role?> GetByIdAsync(Guid roleId, CancellationToken cancellationToken) =>
        db.Roles.Include(r => r.Permissions).FirstOrDefaultAsync(r => r.Id == roleId, cancellationToken);

    public Task<Role?> FindByNameAsync(string name, CancellationToken cancellationToken) =>
        db.Roles.FirstOrDefaultAsync(r => r.Name == name, cancellationToken);

    public async Task<IReadOnlyList<Role>> ListAsync(int? statusId, CancellationToken cancellationToken) =>
        await db.Roles.AsNoTracking()
            .Include(r => r.Permissions)
            .Where(r => statusId == null || r.StatusId == statusId)
            .OrderBy(r => r.Name)
            .ToListAsync(cancellationToken);

    public void Add(Role role) => db.Roles.Add(role);

    public void AddPermission(RolePermission permission) => db.RolePermissions.Add(permission);
}
