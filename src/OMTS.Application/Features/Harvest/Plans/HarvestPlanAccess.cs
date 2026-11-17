using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Domain.Entities.Harvest;

namespace OMTS.Application.Features.Harvest.Plans;

/// <summary>
/// Who may do what with the harvest plans. A plan has the zones of the rafts of its lines: the Owner does everything; a Zone Manager reads the plans handed over that have a line in one of its zones, sees only those
/// lines and harvests from them with HARVESTS Create (never a Draft); the Accountant has none. Changing a plan needs the right in every zone it spans.
/// </summary>
public sealed class HarvestPlanAccess(ZoneAccessGuard zoneAccess)
{
    /// <summary>What a list may show the caller: the lines of every zone (null) or of some, and whether a plan never handed over is hidden.</summary>
    public sealed record Scope(bool IsEmpty, IReadOnlySet<Guid>? ZoneIds, bool AssignedOnly);

    public Task<bool> IsOwnerAsync(CancellationToken cancellationToken) => zoneAccess.IsCooperativeOwnerAsync(cancellationToken);

    /// <summary>The distinct zones of the lines of a plan, by line number.</summary>
    public static IReadOnlyList<Guid> ZoneIdsOf(HarvestPlan plan) => plan.Lines.OrderBy(l => l.LineNumber).Select(l => l.Raft.ZoneId).Distinct().ToList();

    /// <summary>The caller holds <paramref name="action"/> on HARVEST_PLANS in every one of the zones; the Owner always. With no zone at all only the Owner may.</summary>
    public async Task<bool> MayWriteAsync(IEnumerable<Guid> zoneIds, PermissionAction action, CancellationToken cancellationToken)
    {
        if (await IsOwnerAsync(cancellationToken))
        {
            return true;
        }

        var zones = zoneIds.Distinct().ToList();
        foreach (var zoneId in zones)
        {
            if (!await zoneAccess.AllowsAsync(PermissionFunctions.HarvestPlans, action, zoneId, cancellationToken))
            {
                return false;
            }
        }

        return zones.Count > 0;
    }

    /// <summary>The caller may record harvests and leave lanes out in the zone (HARVESTS Create).</summary>
    public Task<bool> MayHarvestAsync(Guid zoneId, CancellationToken cancellationToken) =>
        zoneAccess.AllowsAsync(PermissionFunctions.Harvests, PermissionAction.Create, zoneId, cancellationToken);

    public async Task<Scope> ScopeAsync(PermissionAction action, CancellationToken cancellationToken)
    {
        if (await IsOwnerAsync(cancellationToken))
        {
            return new Scope(false, null, false);
        }

        var zones = await zoneAccess.PermittedZonesAsync(PermissionFunctions.HarvestPlans, action, cancellationToken);
        return new Scope(zones.IsEmpty, zones.AllZones ? null : zones.ZoneIds, true);
    }

    /// <summary>The zones the caller may use for <paramref name="action"/> on HARVEST_PLANS, the Owner every one.</summary>
    public async Task<PermittedZones> ZonesAsync(PermissionAction action, CancellationToken cancellationToken) =>
        await IsOwnerAsync(cancellationToken) ? PermittedZones.Everywhere : await zoneAccess.PermittedZonesAsync(PermissionFunctions.HarvestPlans, action, cancellationToken);

    /// <summary>The lines of the plan the caller sees: every line for the Owner, for a Zone Manager the lines of its zones, and none while the plan was never handed over.</summary>
    public async Task<IReadOnlyList<HarvestPlanLine>> VisibleLinesAsync(HarvestPlan plan, CancellationToken cancellationToken) =>
        VisibleLines(plan, await ScopeAsync(PermissionAction.View, cancellationToken));

    public static IReadOnlyList<HarvestPlanLine> VisibleLines(HarvestPlan plan, Scope scope) =>
        scope.IsEmpty || (scope.AssignedOnly && plan.AssignedAt is null)
            ? []
            : plan.Lines.Where(l => scope.ZoneIds is null || scope.ZoneIds.Contains(l.Raft.ZoneId)).ToList();

    /// <summary>The caller sees the plan: the Owner every one, a Zone Manager the plans that were handed over with a line in one of its zones.</summary>
    public async Task<bool> MayReadAsync(HarvestPlan plan, CancellationToken cancellationToken) =>
        (await VisibleLinesAsync(plan, cancellationToken)).Count > 0 || (plan.Lines.Count == 0 && await IsOwnerAsync(cancellationToken));

    /// <summary>The buttons the caller gets on a plan with these zones.</summary>
    public async Task<HarvestPlanCaller> CallerAsync(IReadOnlyCollection<Guid> planZoneIds, CancellationToken cancellationToken)
    {
        if (await IsOwnerAsync(cancellationToken))
        {
            return new HarvestPlanCaller(true, true, true);
        }

        return new HarvestPlanCaller(
            false,
            await MayWriteAsync(planZoneIds, PermissionAction.Edit, cancellationToken),
            await MayWriteAsync(planZoneIds, PermissionAction.Delete, cancellationToken));
    }

    /// <summary>The zones the caller may harvest in (HARVESTS View), for the plans to harvest from; null: every zone.</summary>
    public async Task<(bool IsEmpty, IReadOnlySet<Guid>? ZoneIds)> HarvestZonesAsync(CancellationToken cancellationToken)
    {
        if (await IsOwnerAsync(cancellationToken))
        {
            return (false, null);
        }

        var zones = await zoneAccess.PermittedZonesAsync(PermissionFunctions.Harvests, PermissionAction.View, cancellationToken);
        return (zones.IsEmpty, zones.AllZones ? null : zones.ZoneIds);
    }
}
