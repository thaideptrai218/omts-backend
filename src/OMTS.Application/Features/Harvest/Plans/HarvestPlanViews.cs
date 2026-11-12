using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.WaterQuality.Dtos;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Application.Features.Harvest.Plans;

/// <summary>
/// Builds the answers: one plan with its lines, lanes, orders and buttons, and the rows of the list. The plan comes with its lines (raft and zone, lanes, product, lot) and orders. A Zone Manager gets only the lines of
/// its own zones, so the zones, the progress and the buttons are those of what it sees.
/// </summary>
public sealed class HarvestPlanViews(IAccountRepository accounts, IStatusCatalog statusCatalog, HarvestPlanAccess access)
{
    /// <param name="harvestZones">When given (the harvest screen), only the lines of these zones, among those the caller sees.</param>
    public async Task<HarvestPlanView> ViewAsync(HarvestPlan plan, CancellationToken cancellationToken, IReadOnlySet<Guid>? harvestZones = null)
    {
        var scope = await access.ScopeAsync(PermissionAction.View, cancellationToken);
        var visible = HarvestPlanAccess.VisibleLines(plan, scope).Where(l => harvestZones is null || harvestZones.Contains(l.Raft.ZoneId)).ToList();
        var caller = await access.CallerAsync(HarvestPlanAccess.ZoneIdsOf(plan), cancellationToken);
        var ids = new[] { plan.CreatedByAccountId, plan.AssignedByAccountId, plan.CancelledByAccountId }.OfType<Guid>()
            .Concat(visible.SelectMany(l => l.Lanes).Select(l => l.SkippedByAccountId).OfType<Guid>()).Distinct().ToList();
        var names = ids.Count == 0 ? new Dictionary<Guid, string>() : await accounts.GetDisplayNamesAsync(ids, cancellationToken);
        AccountRef? Ref(Guid? id) => id is { } value ? new AccountRef(value, names.GetValueOrDefault(value) ?? string.Empty) : null;

        var inProgress = HarvestPlanRules.AllowsHarvest(plan.StatusId);
        var mayHarvest = new Dictionary<Guid, bool>();
        foreach (var zoneId in visible.Select(l => l.Raft.ZoneId).Distinct())
        {
            mayHarvest[zoneId] = await access.MayHarvestAsync(zoneId, cancellationToken);
        }

        var lineViews = new List<HarvestPlanLineView>();
        var canHarvestNow = false;
        foreach (var line in visible.OrderBy(l => l.LineNumber))
        {
            var fact = HarvestPlanRules.FactsOf(line);
            var inLot = line.Lot?.Lanes.Select(l => l.LaneId).ToHashSet() ?? [];
            var lanes = line.Lanes.OrderBy(l => l.Lane.LaneNumber).Select(l => new HarvestPlanLaneView(
                l.LaneId, l.Lane.LaneNumber, HarvestPlanRules.LaneState(inLot.Contains(l.LaneId), l.IsSkipped), inLot.Contains(l.LaneId) ? line.Lot?.LotCode : null,
                l.SkippedAt, Ref(l.SkippedByAccountId), l.SkipReason)).ToList();
            var canHarvest = inProgress && !HarvestPlanRules.LineDone(fact) && mayHarvest[line.Raft.ZoneId];
            canHarvestNow |= canHarvest;
            lineViews.Add(new HarvestPlanLineView(
                line.Id, line.LineNumber, line.RaftId, line.Raft.Code, ZoneView(line.Raft.Zone),
                line.ExpectedProduct is { } p ? new HarvestPlanProductView(p.Id, p.Code, p.Name) : null, line.ExpectedCageCount, line.Note,
                HarvestPlanRules.LineState(fact),
                line.Lot is { } lot ? new HarvestPlanLotView(lot.Id, lot.LotCode, lot.CageCount, lot.EstimatedKg, lot.HarvestedAt) : null,
                lanes, canHarvest, HarvestPlanRules.LineLocked(fact)));
        }

        var hasLot = plan.Lines.Any(l => l.Lot is not null);
        return new HarvestPlanView(
            plan.Id, plan.Code, plan.StatusId, statusCatalog.DisplayNameOf(plan.StatusId), ZonesOf(visible), plan.PlannedAt, plan.Note,
            Ref(plan.CreatedByAccountId), plan.AssignedAt, Ref(plan.AssignedByAccountId), plan.CompletedAt, plan.CancelledAt, Ref(plan.CancelledByAccountId), plan.CancelReason,
            lineViews, OrderViews(plan), Progress(visible),
            HarvestPlanRules.AllowedActions(plan.StatusId, caller, hasLot, canHarvestNow),
            plan.CreatedAt, RowVersionGuard.Encode(plan.RowVersion));
    }

    public async Task<IReadOnlyList<HarvestPlanListItem>> ListItemsAsync(IReadOnlyList<HarvestPlan> plans, CancellationToken cancellationToken)
    {
        var ids = plans.Select(p => p.CreatedByAccountId).Distinct().ToList();
        var names = ids.Count == 0 ? new Dictionary<Guid, string>() : await accounts.GetDisplayNamesAsync(ids, cancellationToken);
        var scope = await access.ScopeAsync(PermissionAction.View, cancellationToken);
        return plans.Select(p =>
        {
            var visible = HarvestPlanAccess.VisibleLines(p, scope);
            return new HarvestPlanListItem(
                p.Id, p.Code, p.StatusId, statusCatalog.DisplayNameOf(p.StatusId), ZonesOf(visible), p.PlannedAt, p.Note,
                visible.OrderBy(l => l.LineNumber).Select(l => l.Raft.Code).Distinct().ToList(), OrderViews(p), Progress(visible),
                names.GetValueOrDefault(p.CreatedByAccountId), RowVersionGuard.Encode(p.RowVersion));
        }).ToList();
    }

    public HarvestPlanOrderView OrderView(SalesOrder order) => new(
        order.Id, order.Code, order.StatusId, statusCatalog.DisplayNameOf(order.StatusId),
        order.Customer is { } c ? new HarvestPlanCustomerView(c.Id, c.Code, c.Name) : null);

    private static HarvestPlanZoneView ZoneView(Domain.Entities.Farming.FarmingZone zone) => new(zone.Id, zone.Code, zone.Name);

    /// <summary>The distinct zones of the lines, by zone code.</summary>
    private static IReadOnlyList<HarvestPlanZoneView> ZonesOf(IEnumerable<HarvestPlanLine> lines) =>
        lines.Select(l => l.Raft.Zone).DistinctBy(z => z.Id).OrderBy(z => z.Code).Select(ZoneView).ToList();

    private IReadOnlyList<HarvestPlanOrderView> OrderViews(HarvestPlan plan) =>
        plan.Orders.Select(o => o.Order).OrderBy(o => o.Code).Select(OrderView).ToList();

    private static HarvestPlanProgressView Progress(IReadOnlyCollection<HarvestPlanLine> lines)
    {
        var facts = lines.Select(HarvestPlanRules.FactsOf).ToList();
        var lanesHarvested = lines.Where(l => l.Lot is not null).Sum(l => l.Lanes.Count(lane => l.Lot!.Lanes.Any(x => x.LaneId == lane.LaneId)));
        return new HarvestPlanProgressView(facts.Count, facts.Count(HarvestPlanRules.LineDone), facts.Sum(f => f.LaneCount), lanesHarvested, facts.Sum(f => f.SkippedCount));
    }
}
