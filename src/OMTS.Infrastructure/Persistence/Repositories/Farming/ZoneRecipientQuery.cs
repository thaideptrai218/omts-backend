using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Authorization;
using OMTS.Domain.Constants;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Persistence.Repositories.Identity;

namespace OMTS.Infrastructure.Persistence.Repositories.Farming;

internal sealed class ZoneRecipientQuery(OmtsDbContext db, TimeProvider timeProvider) : IZoneRecipientQuery
{
    public async Task<IReadOnlyList<Guid>> GetRecipientsAsync(
        Guid zoneId, string functionCode, PermissionAction action, CancellationToken cancellationToken)
    {
        var today = timeProvider.Today();
        // Same rule as AccountAccess: cooperative-wide functions only count grants at All zones.
        var allZonesOnly = PermissionFunctions.IsCooperativeWide(functionCode);

        var owners = await db.Accounts.AsNoTracking()
            .Where(a => a.SubjectType == SubjectType.CooperativeOwner && a.StatusId == StatusIds.Account.Active)
            .Select(a => a.Id)
            .ToListAsync(cancellationToken);

        // Same effectiveness rule as AccessGrantRepository: Effective, within its dates, role In Use.
        var grants = await db.AccessGrants.AsNoTracking()
            .Where(g => g.StatusId == StatusIds.AccessGrant.Effective
                && g.EffectiveFrom <= today
                && (g.EffectiveTo == null || g.EffectiveTo >= today)
                && g.Role.StatusId == StatusIds.Usage.InUse
                && (g.ZoneId == null || (!allZonesOnly && g.ZoneId == zoneId))
                && g.Account.SubjectType == SubjectType.Staff
                && g.Account.StatusId == StatusIds.Account.Active)
            .Select(g => new { g.AccountId, g.RoleId })
            .ToListAsync(cancellationToken);

        var roleIds = grants.Select(g => g.RoleId).Distinct().ToList();
        var allowedRoles = (await db.RolePermissions.AsNoTracking()
                .Where(p => roleIds.Contains(p.RoleId) && p.FunctionCode == functionCode)
                .ToListAsync(cancellationToken))
            .Where(p => new GrantedPermission(p.FunctionCode, p.CanView, p.CanCreate, p.CanEdit, p.CanDelete, p.OtherPermissions, p.DataScope)
                .Allows(action))
            .Select(p => p.RoleId)
            .ToHashSet();

        return owners
            .Concat(grants.Where(g => allowedRoles.Contains(g.RoleId)).Select(g => g.AccountId))
            .Distinct()
            .ToList();
    }
}
