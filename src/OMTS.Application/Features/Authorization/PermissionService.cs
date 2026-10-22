using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Authorization.Dtos;
using OMTS.Application.Features.Authorization.Models;

namespace OMTS.Application.Features.Authorization;

public sealed class PermissionService(
    IAccountRepository accounts,
    IAccessGrantRepository grants,
    IFarmingZoneRepository zones,
    PermissionCache cache,
    TimeProvider timeProvider)
{
    /// <summary>Effective access of an account today, or null when the account does not exist.</summary>
    public async Task<AccountAccess?> GetAccessAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var today = timeProvider.Today();
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
        var zonesById = (await zones.GetByIdsAsync(zoneIds, cancellationToken)).ToDictionary(z => z.Id);

        var grantViews = access.Grants
            .Select(g => g.ZoneId is { } zoneId && zonesById.TryGetValue(zoneId, out var zone)
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
            grantViews);
    }

    private async Task<AccountAccess?> LoadAsync(Guid accountId, DateOnly today, CancellationToken cancellationToken)
    {
        var account = await accounts.GetByIdAsync(accountId, cancellationToken);
        if (account is null)
        {
            return null;
        }

        var zoneGrants = (await grants.GetEffectiveGrantsAsync(accountId, today, cancellationToken))
            .Select(g => new ZoneGrant(
                g.ZoneId,
                g.RoleId,
                g.RoleName,
                g.Permissions
                    .Select(p => new GrantedPermission(p.FunctionCode, p.CanView, p.CanCreate, p.CanEdit, p.CanDelete, p.OtherPermissions, p.DataScope))
                    .ToList()))
            .ToList();

        return new AccountAccess(accountId, account.SubjectType, account.MustChangePassword, account.PhoneVerifiedAt is not null, zoneGrants);
    }
}
