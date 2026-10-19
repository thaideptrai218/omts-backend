using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces;
using OMTS.Domain.Statuses;

namespace OMTS.Application.Authorization;

public sealed record FunctionAccess(string Function, string Name, bool AllZones, IReadOnlyCollection<Guid> ZoneIds, IReadOnlyList<string> Actions);

public sealed record GrantView(Guid? ZoneId, string? ZoneCode, string? ZoneName, bool AllZones, string Role, IReadOnlyList<GrantedPermission> Permissions);

/// <summary>Response of GET /me/permissions, used to build web/mobile menus.</summary>
public sealed record MyPermissions(
    Guid AccountId,
    string SubjectType,
    bool IsCooperativeOwner,
    bool RequiresPasswordChange,
    bool RequiresPhoneVerification,
    IReadOnlyList<FunctionAccess> Functions,
    IReadOnlyList<GrantView> Grants);

public sealed class PermissionService(IOmtsDbContext db, PermissionCache cache, TimeProvider timeProvider)
{
    /// <summary>Effective access of an account today, or null when the account does not exist.</summary>
    public async Task<AccountAccess?> GetAccessAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        var key = cache.KeyFor(accountId, today);
        if (cache.Get(key) is { } cached)
        {
            return cached;
        }

        var access = await LoadAsync(accountId, today, cancellationToken);
        if (access is not null)
        {
            cache.Set(key, access);
        }

        return access;
    }

    public async Task<MyPermissions?> GetMyPermissionsAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var access = await GetAccessAsync(accountId, cancellationToken);
        if (access is null)
        {
            return null;
        }

        var zoneIds = access.Grants.Where(g => g.ZoneId is not null).Select(g => g.ZoneId!.Value).Distinct().ToList();
        var zones = await db.FarmingZones.AsNoTracking()
            .Where(z => zoneIds.Contains(z.Id))
            .Select(z => new { z.Id, z.Code, z.Name })
            .ToDictionaryAsync(z => z.Id, cancellationToken);

        var grants = access.Grants
            .Select(g => g.ZoneId is { } zoneId && zones.TryGetValue(zoneId, out var zone)
                ? new GrantView(zoneId, zone.Code, zone.Name, false, g.RoleName, g.Permissions)
                : new GrantView(g.ZoneId, null, null, g.ZoneId is null, g.RoleName, g.Permissions))
            .ToList();

        var functions = new List<FunctionAccess>();
        foreach (var function in PermissionFunctions.All)
        {
            var actions = Enum.GetValues<PermissionAction>()
                .Select(action => (action, zones: access.PermittedZonesFor(function.Code, action)))
                .Where(x => !x.zones.IsEmpty)
                .ToList();
            if (actions.Count == 0)
            {
                continue;
            }

            var allZones = actions.Any(x => x.zones.AllZones);
            var permittedZoneIds = allZones ? new List<Guid>() : actions.SelectMany(x => x.zones.ZoneIds).Distinct().ToList();
            functions.Add(new FunctionAccess(function.Code, function.Name, allZones, permittedZoneIds, actions.Select(x => x.action.ToString()).ToList()));
        }

        return new MyPermissions(
            access.AccountId,
            access.SubjectType.ToString(),
            access.IsCooperativeOwner,
            access.RequiresPasswordChange,
            access.RequiresPhoneVerification,
            functions,
            grants);
    }

    private async Task<AccountAccess?> LoadAsync(Guid accountId, DateOnly today, CancellationToken cancellationToken)
    {
        var account = await db.Accounts.AsNoTracking()
            .Where(a => a.Id == accountId)
            .Select(a => new { a.SubjectType, a.MustChangePassword, PhoneVerified = a.PhoneVerifiedAt != null })
            .FirstOrDefaultAsync(cancellationToken);
        if (account is null)
        {
            return null;
        }

        // Only Effective grants within their validity period; Revoked or expired ones never count (BR-19).
        var grants = await db.AccessGrants.AsNoTracking()
            .Where(g => g.AccountId == accountId
                && g.StatusId == StatusIds.AccessGrant.Effective
                && g.EffectiveFrom <= today
                && (g.EffectiveTo == null || g.EffectiveTo >= today)
                && g.Role.StatusId == StatusIds.Usage.InUse)
            .Select(g => new { g.ZoneId, g.RoleId, RoleName = g.Role.Name })
            .ToListAsync(cancellationToken);

        var roleIds = grants.Select(g => g.RoleId).Distinct().ToList();
        var permissionsByRole = (await db.RolePermissions.AsNoTracking()
                .Where(p => roleIds.Contains(p.RoleId))
                .ToListAsync(cancellationToken))
            .GroupBy(p => p.RoleId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<GrantedPermission>)g
                    .Select(p => new GrantedPermission(p.FunctionCode, p.CanView, p.CanCreate, p.CanEdit, p.CanDelete, p.OtherPermissions, p.DataScope))
                    .ToList());

        var zoneGrants = grants
            .Select(g => new ZoneGrant(g.ZoneId, g.RoleId, g.RoleName, permissionsByRole.GetValueOrDefault(g.RoleId) ?? []))
            .ToList();

        return new AccountAccess(accountId, account.SubjectType, account.MustChangePassword, account.PhoneVerified, zoneGrants);
    }
}
