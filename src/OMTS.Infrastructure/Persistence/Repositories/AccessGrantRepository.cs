using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;

namespace OMTS.Infrastructure.Persistence.Repositories;

internal sealed class AccessGrantRepository(OmtsDbContext db) : IAccessGrantRepository
{
    public Task<bool> HasEffectiveGrantAsync(Guid accountId, CancellationToken cancellationToken) =>
        db.AccessGrants.Where(g => g.AccountId == accountId).EffectiveOfFixedRoles().AnyAsync(cancellationToken);

    public async Task<IReadOnlyList<ZoneGrant>> GetEffectiveGrantsAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var grants = await db.AccessGrants.AsNoTracking()
            .Where(g => g.AccountId == accountId)
            .EffectiveOfFixedRoles()
            .Select(g => new { g.ZoneId, g.RoleId, g.Role.Code, g.Role.Name })
            .ToListAsync(cancellationToken);

        // The permissions come from the static matrix of the fixed role (BR-FARM-07), never from stored rows.
        return grants
            .Select(g => new ZoneGrant(g.ZoneId, g.RoleId, g.Code, g.Name, FixedRoles.FindByCode(g.Code)!.GrantedPermissions))
            .ToList();
    }

    public async Task<IReadOnlyList<AccessGrant>> ListForAccountAsync(Guid accountId, CancellationToken cancellationToken) =>
        await db.AccessGrants.AsNoTracking()
            .Include(g => g.Role)
            .Include(g => g.Zone)
            .Where(g => g.AccountId == accountId)
            .OrderByDescending(g => g.CreatedAt)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<AccessGrant>> ListEffectiveForAccountAsync(Guid accountId, CancellationToken cancellationToken) =>
        await db.AccessGrants.AsNoTracking()
            .Include(g => g.Role)
            .Include(g => g.Zone)
            .Where(g => g.AccountId == accountId)
            .EffectiveOfFixedRoles()
            .OrderByDescending(g => g.CreatedAt)
            .ToListAsync(cancellationToken);

    public Task<AccessGrant?> GetAsync(Guid grantId, Guid accountId, CancellationToken cancellationToken) =>
        db.AccessGrants.FirstOrDefaultAsync(g => g.Id == grantId && g.AccountId == accountId, cancellationToken);

    public Task<AccessGrant?> FindDuplicateAsync(Guid accountId, Guid roleId, Guid? zoneId, CancellationToken cancellationToken) =>
        db.AccessGrants.AsNoTracking().FirstOrDefaultAsync(
            g => g.AccountId == accountId
                && g.RoleId == roleId
                && g.StatusId == StatusIds.AccessGrant.Effective
                && (g.ZoneId == null || g.ZoneId == zoneId),
            cancellationToken);

    public void Add(AccessGrant grant) => db.AccessGrants.Add(grant);
}
