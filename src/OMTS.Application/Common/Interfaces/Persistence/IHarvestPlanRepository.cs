using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>
/// Filters of the plan list; null means "any". A plan has the zones of the rafts of its lines: <paramref name="ZoneId"/> keeps the plans with a line in that zone, and the caller's reach <paramref name="ZoneIds"/> (null: every zone)
/// the plans with a line in one of them. <paramref name="AssignedOnly"/> hides the plans that were never handed over (what a Zone Manager sees).
/// </summary>
public sealed record HarvestPlanFilter(
    string? Search, IReadOnlyList<int>? StatusIds, DateOnly? From, DateOnly? To, Guid? ZoneId, Guid? SalesOrderId, IReadOnlySet<Guid>? ZoneIds, bool AssignedOnly);

/// <summary>A lane waiting to be harvested in an open plan (no lot on its line, not left out).</summary>
public sealed record OpenLaneClaim(Guid PlanId, string PlanCode, Guid RaftId, string RaftCode, Guid LaneId, int LaneNumber);

/// <summary>The plan a lot was recorded from, with the orders the plan serves.</summary>
public sealed record PlanOfLot(Guid LotId, Guid PlanId, string PlanCode, IReadOnlyList<Guid> OrderIds);

/// <summary>The lanes planned on a line, with the code of its plan: what a lot recorded from the line may hold.</summary>
public sealed record PlannedLanes(string PlanCode, IReadOnlySet<Guid> LaneIds);

/// <summary>A plan serving an order.</summary>
public sealed record PlanOfOrder(Guid OrderId, Guid PlanId, string PlanCode, int StatusId, DateTimeOffset PlannedAt, bool WasAssigned);

public interface IHarvestPlanRepository
{
    /// <summary>The plan with its lines (raft and its zone, lanes, expected product, lot with its lanes) and orders (with their customers), tracked.</summary>
    Task<HarvestPlan?> GetForEditAsync(Guid planId, CancellationToken cancellationToken);

    /// <summary>The plan that holds the line, loaded as <see cref="GetForEditAsync"/>, tracked.</summary>
    Task<HarvestPlan?> GetByLineForEditAsync(Guid lineId, CancellationToken cancellationToken);

    /// <summary>The plan that has this code regardless of letter case, other than <paramref name="exceptPlanId"/>; cancelled plans included; untracked, header only.</summary>
    Task<HarvestPlan?> FindByCodeAsync(string code, Guid? exceptPlanId, CancellationToken cancellationToken);

    /// <summary>One page of the list, code descending, each plan loaded as <see cref="GetForEditAsync"/> but untracked.</summary>
    Task<(IReadOnlyList<HarvestPlan> Items, int Total)> SearchAsync(HarvestPlanFilter filter, int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>The plans Assigned or Harvesting with a line in these zones (null: every zone), planned time first, loaded as <see cref="GetForEditAsync"/> but untracked.</summary>
    Task<IReadOnlyList<HarvestPlan>> ListInProgressAsync(IReadOnlySet<Guid>? zoneIds, CancellationToken cancellationToken);

    /// <summary>The lanes of these rafts that wait to be harvested in a plan that is Draft, Assigned or Harvesting, other than <paramref name="exceptPlanId"/>.</summary>
    Task<IReadOnlyList<OpenLaneClaim>> OpenLaneClaimsAsync(IReadOnlyCollection<Guid> raftIds, Guid? exceptPlanId, CancellationToken cancellationToken);

    /// <summary>
    /// The product each of these rafts farms in its Open cycle (<c>CultivationCycle.ProductId</c>); a raft with no Open cycle, or an Open cycle started before products were recorded, is absent.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, Guid>> CycleProductsAsync(IReadOnlyCollection<Guid> raftIds, CancellationToken cancellationToken);

    /// <summary>The plan each of these lots was recorded from, with its orders; a lot outside a plan is absent.</summary>
    Task<IReadOnlyList<PlanOfLot>> PlansOfLotsAsync(IReadOnlyCollection<Guid> lotIds, CancellationToken cancellationToken);

    /// <summary>The lanes planned on the line and its plan code, untracked; null when there is no such line.</summary>
    Task<PlannedLanes?> PlannedLanesOfLineAsync(Guid lineId, CancellationToken cancellationToken);

    /// <summary>The plans serving these orders.</summary>
    Task<IReadOnlyList<PlanOfOrder>> PlansOfOrdersAsync(IReadOnlyCollection<Guid> orderIds, CancellationToken cancellationToken);

    /// <summary>The orders of these ids with their customers, untracked: what a plan links to.</summary>
    Task<IReadOnlyList<SalesOrder>> OrdersByIdsAsync(IReadOnlyCollection<Guid> orderIds, CancellationToken cancellationToken);

    void Add(HarvestPlan plan);
    void AddLine(HarvestPlanLine line);
    void RemoveLine(HarvestPlanLine line);
    void AddLane(HarvestPlanLane lane);
    void RemoveLane(HarvestPlanLane lane);
    void AddOrderLink(SalesOrderHarvestPlan link);
    void RemoveOrderLink(SalesOrderHarvestPlan link);
}
