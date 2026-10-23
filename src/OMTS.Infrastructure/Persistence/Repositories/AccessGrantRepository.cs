using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Models;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;

namespace OMTS.Infrastructure.Persistence.Repositories;

internal sealed class AccessGrantRepository(OmtsDbContext db) : IAccessGrantRepository
{
    public Task<bool> HasEffectiveGrantAsync(Guid accountId, DateOnly today, CancellationToken cancellationToken) =>
        db.AccessGrants.AnyAsync(IsEffective(accountId, today), cancellationToken);

    public async Task<IReadOnlyList<EffectiveGrant>> GetEffectiveGrantsAsync(Guid accountId, DateOnly today, CancellationToken cancellationToken)
    {
        var grants = await db.AccessGrants.AsNoTracking()
            .Where(IsEffective(accountId, today))
            .Select(g => new { g.ZoneId, g.RoleId, RoleName = g.Role.Name })
            .ToListAsync(cancellationToken);

        var roleIds = grants.Select(g => g.RoleId).Distinct().ToList();
        var permissionsByRole = (await db.RolePermissions.AsNoTracking()
                .Where(p => roleIds.Contains(p.RoleId))
                .ToListAsync(cancellationToken))
            .ToLookup(p => p.RoleId);

        return grants
            .Select(g => new EffectiveGrant(g.ZoneId, g.RoleId, g.RoleName, permissionsByRole[g.RoleId].ToList()))
            .ToList();
    }

    public async Task<IReadOnlyList<AccessGrant>> ListForAccountAsync(Guid accountId, CancellationToken cancellationToken) =>
        await db.AccessGrants.AsNoTracking()
            .Include(g => g.Role)
            .Include(g => g.Zone)
            .Where(g => g.AccountId == accountId)
            .OrderByDescending(g => g.CreatedAt)
            .ToListAsync(cancellationToken);

    public Task<AccessGrant?> GetAsync(Guid grantId, Guid accountId, CancellationToken cancellationToken) =>
        db.AccessGrants.FirstOrDefaultAsync(g => g.Id == grantId && g.AccountId == accountId, cancellationToken);

    public Task<AccessGrant?> FindOverlappingAsync(
        Guid accountId, Guid? zoneId, Guid roleId, DateOnly from, DateOnly? to, CancellationToken cancellationToken) =>
        // Two periods overlap when each starts before the other ends (an open end never ends).
        db.AccessGrants.FirstOrDefaultAsync(
            g => g.AccountId == accountId
                && g.ZoneId == zoneId
                && g.RoleId == roleId
                && g.StatusId == StatusIds.AccessGrant.Effective
                && (to == null || g.EffectiveFrom <= to)
                && (g.EffectiveTo == null || g.EffectiveTo >= from),
            cancellationToken);

    public async Task<IReadOnlyList<AccessGrant>> GetCurrentOrFutureForRoleAsync(Guid roleId, DateOnly today, CancellationToken cancellationToken) =>
        await db.AccessGrants.AsNoTracking()
            .Include(g => g.Account)
            .Where(g => g.RoleId == roleId
                && g.StatusId == StatusIds.AccessGrant.Effective
                && (g.EffectiveTo == null || g.EffectiveTo >= today))
            .ToListAsync(cancellationToken);

    public void Add(AccessGrant grant) => db.AccessGrants.Add(grant);

    // Revoked or expired grants never count, nor grants of a discontinued role (BR-19).
    private static Expression<Func<AccessGrant, bool>> IsEffective(Guid accountId, DateOnly today) =>
        g => g.AccountId == accountId
            && g.StatusId == StatusIds.AccessGrant.Effective
            && g.EffectiveFrom <= today
            && (g.EffectiveTo == null || g.EffectiveTo >= today)
            && g.Role.StatusId == StatusIds.Usage.InUse;
}
