using Microsoft.Extensions.Caching.Memory;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.UnitTests.Features.Purchasing;
using OMTS.Domain.Enums;

namespace OMTS.Application.UnitTests.Features.Authorization;

/// <summary>A zone guard over a caller whose access a test states, for the services that limit a list to the caller's zones.</summary>
internal static class ZoneAccessKit
{
    public static ZoneAccessGuard Guard(AccountAccess access)
    {
        var cache = new PermissionCache(new MemoryCache(new MemoryCacheOptions()));
        cache.Set(cache.KeyFor(access.AccountId), access);
        var permissions = new PermissionService(StubProxy.Create<IAccountRepository>(_ => { }), StubProxy.Create<IAccessGrantRepository>(_ => { }), StubProxy.Create<IFarmingZoneRepository>(_ => { }), cache);
        return new ZoneAccessGuard(new Fakes.FakeCurrentUser(access.AccountId), permissions);
    }

    public static AccountAccess Owner() => new(Guid.NewGuid(), SubjectType.CooperativeOwner, false, true, []);

    /// <summary>A staff caller who may view <paramref name="function"/> in the zones given and in no other.</summary>
    public static AccountAccess Viewer(string function, params Guid[] zones) => new(
        Guid.NewGuid(), SubjectType.Staff, false, true,
        zones.Select(z => new ZoneGrant(z, Guid.NewGuid(), "CUSTOM", "Viewer", [GrantedPermission.Of(function, [PermissionAction.View])])).ToList());
}
