using OMTS.Application.Common.Interfaces;
using OMTS.Application.Features.Authorization.Models;

namespace OMTS.Application.Features.Authorization;

/// <summary>
/// Zone check of the caller inside a use case, for endpoints addressed by record id where the route has
/// no zone for <c>RequirePermissionAttribute</c>: load the record, then check its zone with the same rule
/// (UC02 postconditions: each function re-checks the record).
/// </summary>
public sealed class ZoneAccessGuard(ICurrentUser currentUser, PermissionService permissions)
{
    public async Task<bool> AllowsAsync(string function, PermissionAction action, Guid zoneId, CancellationToken cancellationToken) =>
        await AccessAsync(cancellationToken) is { } access && access.Allows(function, action, PermissionScope.ZoneScoped, zoneId);

    /// <summary>Zones a list may return for the caller.</summary>
    public async Task<PermittedZones> PermittedZonesAsync(string function, PermissionAction action, CancellationToken cancellationToken) =>
        (await AccessAsync(cancellationToken))?.PermittedZonesFor(function, action) ?? PermittedZones.None;

    /// <summary>The zones of the caller's pairs whose role allows the action (see <see cref="AccountAccess.PairZonesFor"/>).</summary>
    public async Task<PermittedZones> PairZonesAsync(string function, PermissionAction action, CancellationToken cancellationToken) =>
        (await AccessAsync(cancellationToken))?.PairZonesFor(function, action) ?? PermittedZones.None;

    /// <summary>The caller is a Cooperative Owner (every record, and the Owner-only actions).</summary>
    public async Task<bool> IsCooperativeOwnerAsync(CancellationToken cancellationToken) =>
        (await AccessAsync(cancellationToken))?.IsCooperativeOwner == true;

    private async Task<AccountAccess?> AccessAsync(CancellationToken cancellationToken) =>
        currentUser.AccountId is { } accountId ? await permissions.GetAccessAsync(accountId, cancellationToken) : null;
}
