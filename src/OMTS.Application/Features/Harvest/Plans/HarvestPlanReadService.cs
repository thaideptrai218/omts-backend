using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Common.Validation;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Cultivation.Progress;
using OMTS.Application.Features.Harvest;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Application.Features.Harvest.Plans;

/// <summary>The reads of the plans: the list, one plan by id or code, the code the next plan would get, the rafts a plan can be made for, the form of a plan made from an order, and the plans a Zone Manager can harvest from.</summary>
public sealed class HarvestPlanReadService(
    IHarvestPlanRepository plans,
    ISalesOrderRepository orders,
    IProductRepository products,
    IFarmingZoneRepository zones,
    IRaftRepository rafts,
    ICultivationCycleQuery cycleQuery,
    LaneStateReader laneStates,
    TimeProvider timeProvider,
    HarvestPlanAccess access,
    HarvestPlanCodeService codes,
    HarvestPlanViews views,
    IStatusCatalog statusCatalog)
{
    private const int MaxPageSize = 100;

    private static readonly int[] StatusIdsOfPlans =
        [StatusIds.HarvestPlan.Draft, StatusIds.HarvestPlan.Assigned, StatusIds.HarvestPlan.Harvesting, StatusIds.HarvestPlan.Completed, StatusIds.HarvestPlan.Cancelled];

    public async Task<Result<HarvestPlanPage>> SearchAsync(HarvestPlanListQuery query, CancellationToken cancellationToken)
    {
        var scope = await access.ScopeAsync(PermissionAction.View, cancellationToken);
        if (scope.IsEmpty)
        {
            return AppError.Forbidden;
        }

        if (query.From > query.To)
        {
            return AppError.Invalid("Period");
        }

        if (query.StatusIds is { } statusIds && statusIds.Any(id => !StatusIdsOfPlans.Contains(id)))
        {
            return AppError.Invalid("Status");
        }

        var text = DisplayNames.Normalize(query.Search);
        var filter = new HarvestPlanFilter(
            text.Length == 0 ? null : text, query.StatusIds is { Count: > 0 } ? query.StatusIds : null, query.From, query.To, query.ZoneId, query.SalesOrderId, scope.ZoneIds, scope.AssignedOnly);
        // The upper bound keeps (page - 1) * pageSize inside int.
        var (page, pageSize) = (Math.Clamp(query.Page, 1, int.MaxValue / MaxPageSize), Math.Clamp(query.PageSize, 1, MaxPageSize));
        var (items, total) = await plans.SearchAsync(filter, page, pageSize, cancellationToken);
        return new HarvestPlanPage(await views.ListItemsAsync(items, cancellationToken), page, pageSize, total);
    }

    public async Task<Result<HarvestPlanView>> GetAsync(Guid planId, CancellationToken cancellationToken)
    {
        var plan = await plans.GetForEditAsync(planId, cancellationToken);
        return plan is null || !await access.MayReadAsync(plan, cancellationToken) ? AppError.NotFound : await views.ViewAsync(plan, cancellationToken);
    }

    /// <summary>The plan with this code regardless of letter case, for the link that opens it by its code.</summary>
    public async Task<Result<HarvestPlanView>> GetByCodeAsync(string code, CancellationToken cancellationToken) =>
        await plans.FindByCodeAsync(code.Trim(), null, cancellationToken) is { } found ? await GetAsync(found.Id, cancellationToken) : AppError.NotFound;

    /// <summary>The code the next plan would get; not reserved.</summary>
    public async Task<Result<HarvestPlanNextCode>> NextCodeAsync(CancellationToken cancellationToken) =>
        !(await access.ScopeAsync(PermissionAction.Create, cancellationToken)).IsEmpty ? new HarvestPlanNextCode(await codes.PeekAsync(cancellationToken)) : AppError.Forbidden;

    /// <summary>The plans Assigned or Harvesting in the zones the caller harvests: what the harvest screen offers to record from.</summary>
    public async Task<Result<HarvestPlansToHarvest>> InProgressAsync(CancellationToken cancellationToken)
    {
        var (isEmpty, zoneIds) = await access.HarvestZonesAsync(cancellationToken);
        if (isEmpty)
        {
            return AppError.Forbidden;
        }

        var found = await plans.ListInProgressAsync(zoneIds, cancellationToken);
        var result = new List<HarvestPlanView>(found.Count);
        foreach (var plan in found)
        {
            // Only the lines of the zones the caller harvests in: a plan spans several zones, each Zone Manager records its own.
            result.Add(await views.ViewAsync(plan, cancellationToken, zoneIds));
        }

        return new HarvestPlansToHarvest(result);
    }

    /// <summary>
    /// The form of a new plan made from an order, for the Owner: its pickup time as the planned time, and its products with the cages worked out (the cages of a Lồng line, or the kg of the line
    /// over the kg of one Lồng of the product, rounded up; none when the product has no Lồng unit). The lines, rafts and lanes, are for the Owner to choose. 409 MSG-GEN-09 when the order is Completed or Cancelled.
    /// </summary>
    public async Task<Result<HarvestPlanOrderDraft>> OrderDraftAsync(Guid orderId, CancellationToken cancellationToken)
    {
        if (!await access.IsOwnerAsync(cancellationToken))
        {
            return AppError.Forbidden;
        }

        var order = await orders.GetForEditAsync(orderId, cancellationToken);
        if (order is null)
        {
            return AppError.NotFound;
        }

        if (order.StatusId is not (StatusIds.SalesOrder.Draft or StatusIds.SalesOrder.Delivering))
        {
            return AppError.Conflict(Common.Messages.UserMessages.WrongStatus($"Sales order {order.Code}", statusCatalog.DisplayNameOf(order.StatusId)));
        }

        var catalogue = (await products.GetByIdsAsync(order.Lines.Select(l => l.ProductId).Distinct().ToList(), cancellationToken)).ToDictionary(p => p.Id);
        var draftProducts = order.Lines.OrderBy(l => l.LineNumber).Select(line => new HarvestPlanDraftProduct(
            line.ProductId, line.Product?.Code ?? string.Empty, line.Product?.Name ?? string.Empty, line.UnitId, line.UnitCode, line.UnitName, line.Quantity,
            CagesOf(line, catalogue.GetValueOrDefault(line.ProductId)))).ToList();
        return new HarvestPlanOrderDraft(views.OrderView(order), order.PickupAt, draftProducts, []);
    }

    /// <summary>
    /// The rafts a plan can be made for, with their lanes (GET eligible-rafts): rafts In Use of a zone In Use (the rule of recording a harvest) with an Open cultivation cycle and at least one lane that can be harvested
    /// (Growing or Being harvested). A lane is Free, waiting in another open plan (InAnotherPlan, with its code) or Harvested in the cycle; lanes with no seed are left out. The product is the one the cycle farms.
    /// Needs HARVEST_PLANS Create; <paramref name="zoneId"/> narrows it to one zone (403 for a zone the caller has no right in), else every zone the caller has the right in.
    /// </summary>
    public async Task<Result<EligibleRafts>> EligibleRaftsAsync(Guid? zoneId, CancellationToken cancellationToken)
    {
        var permitted = await access.ZonesAsync(PermissionAction.Create, cancellationToken);
        if (permitted.IsEmpty || (zoneId is { } chosen && !permitted.Allows(chosen)))
        {
            return AppError.Forbidden;
        }

        var candidateZones = (await zones.ListAsync(includeDiscontinued: false, zoneId is { } one ? new PermittedZones(false, new HashSet<Guid> { one }) : permitted, cancellationToken))
            .Where(z => z.StatusId == StatusIds.Usage.InUse).OrderBy(z => z.Code).ToList();
        var today = timeProvider.Today();
        var found = new List<(HarvestPlanZoneView Zone, Domain.Entities.Farming.Raft Raft, IReadOnlyDictionary<Guid, LaneState> States)>();
        foreach (var zone in candidateZones)
        {
            foreach (var raft in await rafts.ListByZoneAsync(zone.Id, StatusIds.Usage.InUse, cancellationToken))
            {
                if (await cycleQuery.OpenCycleAsync(raft.Id, cancellationToken) is null)
                {
                    continue;
                }

                var states = (await laneStates.ReadAsync(raft, today, cancellationToken)).States;
                if (raft.Lanes.Any(l => l.StatusId == StatusIds.Lane.InUse && LaneStateRules.CanBeHarvested(states[l.Id])))
                {
                    found.Add((new HarvestPlanZoneView(zone.Id, zone.Code, zone.Name), raft, states));
                }
            }
        }

        var raftIds = found.Select(f => f.Raft.Id).ToList();
        var claims = (await plans.OpenLaneClaimsAsync(raftIds, null, cancellationToken)).GroupBy(c => c.LaneId).ToDictionary(g => g.Key, g => g.OrderBy(c => c.PlanCode).First().PlanCode);
        var cycleProducts = await plans.CycleProductsAsync(raftIds, cancellationToken);
        var catalogue = cycleProducts.Count == 0
            ? new Dictionary<Guid, Product>()
            : (await products.GetByIdsAsync(cycleProducts.Values.Distinct().ToList(), cancellationToken)).ToDictionary(p => p.Id);
        var result = found.Select(f => new EligibleRaftView(
            f.Raft.Id, f.Raft.Code, f.Zone,
            cycleProducts.TryGetValue(f.Raft.Id, out var productId) && catalogue.TryGetValue(productId, out var product) ? new HarvestPlanProductView(product.Id, product.Code, product.Name) : null,
            f.Raft.Lanes.Where(l => l.StatusId == StatusIds.Lane.InUse && f.States[l.Id] != LaneState.NotSeeded).OrderBy(l => l.LaneNumber)
                .Select(l => LaneOf(l, f.States[l.Id], claims)).ToList())).ToList();
        return new EligibleRafts(result);
    }

    private static EligibleLaneView LaneOf(Domain.Entities.Farming.Lane lane, LaneState state, IReadOnlyDictionary<Guid, string> claims) =>
        state == LaneState.Harvested
            ? new EligibleLaneView(lane.Id, lane.LaneNumber, EligibleLaneState.Harvested, null)
            : claims.TryGetValue(lane.Id, out var planCode)
                ? new EligibleLaneView(lane.Id, lane.LaneNumber, EligibleLaneState.InAnotherPlan, planCode)
                : new EligibleLaneView(lane.Id, lane.LaneNumber, EligibleLaneState.Free, null);

    /// <summary>Cages for a line of the order: its quantity when the line is in Lồng; else the kg of the line over the kg of one Lồng of the product, rounded up; null without a Lồng unit.</summary>
    internal static int? CagesOf(SalesOrderLine line, Product? product)
    {
        if (HarvestLotProductLineService.IsCageUnit(line.UnitName))
        {
            return CeilingToInt(line.Quantity);
        }

        var cage = product?.Units.FirstOrDefault(u => !u.IsBase && HarvestLotProductLineService.IsCageUnit(u.Unit?.Name));
        if (cage is null || cage.Factor <= 0)
        {
            return null;
        }

        return CeilingToInt(line.Quantity * line.Factor / cage.Factor);
    }

    private static int? CeilingToInt(decimal value) => value > 0 && value <= int.MaxValue ? (int)Math.Ceiling(value) : null;
}
