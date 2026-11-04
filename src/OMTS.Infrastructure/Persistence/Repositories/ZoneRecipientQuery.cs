using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Domain.Constants;
using OMTS.Domain.Enums;

namespace OMTS.Infrastructure.Persistence.Repositories;

internal sealed class ZoneRecipientQuery(OmtsDbContext db) : IZoneRecipientQuery
{
    public async Task<IReadOnlyList<Guid>> GetRecipientsAsync(
        Guid zoneId, string functionCode, PermissionAction action, CancellationToken cancellationToken)
    {
        // Same rule as AccountAccess: the fixed roles whose matrix allows the action, and for records with no zone
        // any pair of such a role (BR-FARM-08).
        var roleCodes = FixedRoles.All
            .Where(r => r.GrantedPermissions.Any(p => p.FunctionCode == functionCode && p.Allows(action)))
            .Select(r => r.Code)
            .ToList();
        var anyZone = PermissionFunctions.IsCooperativeWide(functionCode);

        var owners = await db.Accounts.AsNoTracking()
            .Where(a => a.SubjectType == SubjectType.CooperativeOwner && a.StatusId == StatusIds.Account.Active)
            .Select(a => a.Id)
            .ToListAsync(cancellationToken);

        List<Guid> staff = roleCodes.Count == 0
            ? []
            : await db.AccessGrants.AsNoTracking()
                .EffectiveOfFixedRoles()
                .Where(g => roleCodes.Contains(g.Role.Code)
                    && (anyZone || g.ZoneId == null || g.ZoneId == zoneId)
                    && g.Account.SubjectType == SubjectType.Staff
                    && g.Account.StatusId == StatusIds.Account.Active)
                .Select(g => g.AccountId)
                .ToListAsync(cancellationToken);

        return owners.Concat(staff).Distinct().ToList();
    }
}
