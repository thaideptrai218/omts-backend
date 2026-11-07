using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Authorization.Dtos;

namespace OMTS.Application.Features.Authorization;

public sealed class PermissionService(
    IAccountRepository accounts,
    IAccessGrantRepository grants,
    IFarmingZoneRepository zones,
    PermissionCache cache)
{
    /// <summary>Effective access of an account, or null when the account does not exist.</summary>
    public async Task<AccountAccess?> GetAccessAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var key = cache.KeyFor(accountId);
        if (cache.Get(key) is { } cached)
        {
            return cached;
        }

        var access = await LoadAsync(accountId, cancellationToken);
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
                ? new GrantView(zoneId, zone.Code, zone.Name, false, g.RoleName, g.RoleCode, g.Permissions)
                : new GrantView(g.ZoneId, null, null, g.ZoneId is null, g.RoleName, g.RoleCode, g.Permissions))
            .ToList();

        return new MyPermissions(
            access.AccountId,
            access.SubjectType.ToString(),
            access.IsCooperativeOwner,
            access.RequiresPasswordChange,
            access.RequiresPhoneVerification,
            access.DescribeFunctions(),
            grantViews);
    }

    private async Task<AccountAccess?> LoadAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var account = await accounts.GetByIdAsync(accountId, cancellationToken);
        if (account is null)
        {
            return null;
        }

        // Each Effective pair carries the permissions of its fixed role (FixedRoles).
        var zoneGrants = await grants.GetEffectiveGrantsAsync(accountId, cancellationToken);
        return new AccountAccess(accountId, account.SubjectType, account.MustChangePassword, account.PhoneVerifiedAt is not null, zoneGrants);
    }
}
