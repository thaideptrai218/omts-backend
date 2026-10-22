using OMTS.Application.Common.Interfaces;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Farming;

/// <summary>
/// Who may read zones and rafts (FARM-02, FARM-04 lookups and the raft condition view): the Cooperative
/// Owner everywhere; staff in the zones of their effective zone–role grants, of any role, or everywhere with
/// an All zones grant. Zone A never opens zone B (NFR-01). Traders and public viewers see nothing.
/// </summary>
public sealed class FarmingLookupAccess(ICurrentUser currentUser, PermissionService permissions)
{
    public async Task<PermittedZones> VisibleZonesAsync(CancellationToken cancellationToken)
    {
        if (currentUser.AccountId is not { } accountId || await permissions.GetAccessAsync(accountId, cancellationToken) is not { } access)
        {
            return PermittedZones.None;
        }

        if (access.IsCooperativeOwner)
        {
            return PermittedZones.Everywhere;
        }

        if (access.SubjectType != SubjectType.Staff)
        {
            return PermittedZones.None;
        }

        return access.Grants.Any(g => g.ZoneId is null)
            ? PermittedZones.Everywhere
            : new PermittedZones(false, access.Grants.Select(g => g.ZoneId!.Value).ToHashSet());
    }

    public async Task<bool> IsCooperativeOwnerAsync(CancellationToken cancellationToken) =>
        currentUser.AccountId is { } accountId && (await permissions.GetAccessAsync(accountId, cancellationToken))?.IsCooperativeOwner == true;

    public async Task<bool> CanSeeZoneAsync(Guid zoneId, CancellationToken cancellationToken)
    {
        var visible = await VisibleZonesAsync(cancellationToken);
        return visible.AllZones || visible.ZoneIds.Contains(zoneId);
    }
}
