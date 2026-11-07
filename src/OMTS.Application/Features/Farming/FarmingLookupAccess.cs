using OMTS.Application.Common.Interfaces;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Farming;

/// <summary>
/// Who may read zones and rafts. Zones (FARM-02 lookups): the Cooperative Owner everywhere; staff in the zones of their
/// Effective zone–role pairs, of either fixed role, or everywhere with an All zones pair. Rafts and lanes: see
/// <see cref="VisibleRaftZonesAsync"/>. Zone A never opens zone B (NFR-01). Traders and public viewers see nothing. The
/// grants of <see cref="AccountAccess"/> are only the Effective pairs of a fixed role (the persistence filter of
/// BR-FARM-07), so a pair of an old role row opens no zone.
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

    public async Task<bool> CanSeeZoneAsync(Guid zoneId, CancellationToken cancellationToken) =>
        (await VisibleZonesAsync(cancellationToken)).Allows(zoneId);

    /// <summary>
    /// Zones whose rafts and lanes the caller may read: the Owner everywhere, and the zones of a role that works with
    /// cultivation, which is the Zone Manager (Q27: its CYCLE, HARVEST and WATER screens need them). §I.4.4 gives the
    /// Accountant neither FARM-04 nor CYCLE-05, so its pairs open the zone but not the rafts; the purchase receipt filters
    /// use a code-only lookup instead (<see cref="Purchasing.Receipts.PurchaseReceiptQueryService.RaftLookupAsync"/>).
    /// </summary>
    public async Task<PermittedZones> VisibleRaftZonesAsync(CancellationToken cancellationToken) =>
        currentUser.AccountId is { } accountId && await permissions.GetAccessAsync(accountId, cancellationToken) is { } access
            ? access.PermittedZonesFor(PermissionFunctions.CultivationCycles, PermissionAction.View)
            : PermittedZones.None;
}
