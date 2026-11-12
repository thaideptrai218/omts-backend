using OMTS.Application.Features.WaterQuality.Dtos;

namespace OMTS.Application.Features.Harvest.Plans;

/// <summary>What a caller may press on a plan; see <see cref="HarvestPlanRules.AllowedActions"/>.</summary>
public enum HarvestPlanAction
{
    Edit,
    Assign,
    Finish,
    Cancel,
    Harvest,
    SkipLane,
}

/// <summary>A lane of a plan line: still to harvest, in the line's lot, or left out ("Không thu").</summary>
public enum HarvestPlanLaneState
{
    Planned,
    Harvested,
    Skipped,
}

/// <summary>A line of a plan: still open, done with its lot, or done because every lane is left out.</summary>
public enum HarvestPlanLineState
{
    Open,
    Harvested,
    Skipped,
}

public sealed record HarvestPlanZoneView(Guid Id, string Code, string Name);

public sealed record HarvestPlanProductView(Guid Id, string Code, string Name);

public sealed record HarvestPlanCustomerView(Guid Id, string Code, string Name);

/// <summary>An order the plan serves.</summary>
public sealed record HarvestPlanOrderView(Guid Id, string Code, int StatusId, string Status, HarvestPlanCustomerView? Customer);

/// <summary>A lane of a line. <paramref name="LotCode"/> is the lot the lane is in when it is Harvested.</summary>
public sealed record HarvestPlanLaneView(
    Guid LaneId, int LaneNumber, HarvestPlanLaneState State, string? LotCode, DateTimeOffset? SkippedAt, AccountRef? SkippedBy, string? SkipReason);

/// <summary>The lot recorded from a line.</summary>
public sealed record HarvestPlanLotView(Guid Id, string LotCode, int CageCount, decimal EstimatedKg, DateTimeOffset HarvestedAt);

/// <summary>A line of a plan with its lanes; <paramref name="Zone"/> is the zone of its raft; <paramref name="ExpectedProduct"/> the product of the raft's cultivation cycle. <paramref name="CanHarvest"/>: a lot can be recorded from it now, and lanes left out (the caller still needs HARVESTS Create). <paramref name="Locked"/>: it has a lot or a lane left out, so the Owner cannot change it.</summary>
public sealed record HarvestPlanLineView(
    Guid Id,
    int LineNumber,
    Guid RaftId,
    string RaftCode,
    HarvestPlanZoneView Zone,
    HarvestPlanProductView? ExpectedProduct,
    int? ExpectedCageCount,
    string? Note,
    HarvestPlanLineState State,
    HarvestPlanLotView? Lot,
    IReadOnlyList<HarvestPlanLaneView> Lanes,
    bool CanHarvest,
    bool Locked);

/// <summary>How far the plan is: lines done of all lines, and the lanes harvested, left out and still planned.</summary>
public sealed record HarvestPlanProgressView(int Lines, int LinesDone, int Lanes, int LanesHarvested, int LanesSkipped);

/// <param name="Zones">The zones of the lines the caller sees, by code. A Zone Manager gets only the lines (and so the zones and the progress) of its own zones.</param>
public sealed record HarvestPlanView(
    Guid Id,
    string Code,
    int StatusId,
    string Status,
    IReadOnlyList<HarvestPlanZoneView> Zones,
    DateTimeOffset PlannedAt,
    string? Note,
    AccountRef? CreatedBy,
    DateTimeOffset? AssignedAt,
    AccountRef? AssignedBy,
    DateTimeOffset? CompletedAt,
    DateTimeOffset? CancelledAt,
    AccountRef? CancelledBy,
    string? CancelReason,
    IReadOnlyList<HarvestPlanLineView> Lines,
    IReadOnlyList<HarvestPlanOrderView> Orders,
    HarvestPlanProgressView Progress,
    IReadOnlyList<HarvestPlanAction> AllowedActions,
    DateTimeOffset CreatedAt,
    string RowVersion);

/// <summary>A row of the plan list.</summary>
public sealed record HarvestPlanListItem(
    Guid Id,
    string Code,
    int StatusId,
    string Status,
    IReadOnlyList<HarvestPlanZoneView> Zones,
    DateTimeOffset PlannedAt,
    string? Note,
    IReadOnlyList<string> RaftCodes,
    IReadOnlyList<HarvestPlanOrderView> Orders,
    HarvestPlanProgressView Progress,
    string? CreatedBy,
    string RowVersion);

public sealed record HarvestPlanPage(IReadOnlyList<HarvestPlanListItem> Items, int Page, int PageSize, int Total);

/// <summary>The list query. A Zone Manager sees only the plans handed over that have a line in one of its zones.</summary>
/// <param name="Search">Text in the plan code, a raft code of a line, or the code of an order the plan serves.</param>
/// <param name="StatusIds">Only these statuses (repeat the parameter); every status when empty.</param>
/// <param name="From">The first planned day, included.</param>
/// <param name="To">The last planned day, included.</param>
/// <param name="ZoneId">Only the plans with a line (a raft) in this zone.</param>
public sealed record HarvestPlanListQuery(
    string? Search = null,
    IReadOnlyList<int>? StatusIds = null,
    DateOnly? From = null,
    DateOnly? To = null,
    Guid? ZoneId = null,
    Guid? SalesOrderId = null,
    int Page = 1,
    int PageSize = 20);

public sealed record HarvestPlanNextCode(string Code);

/// <summary>A line of a saved plan as sent: a raft of any zone, its lanes and the cages expected (the product is that of the raft's cultivation cycle; an <c>expectedProductId</c> sent is ignored). <paramref name="Id"/> names a line of the plan being updated; without it the line is new.</summary>
/// <param name="LaneNumbers">The lanes to harvest, each given once; at least one.</param>
public sealed record SaveHarvestPlanLineRequest(Guid? Id, Guid? RaftId, IReadOnlyList<int>? LaneNumbers, int? ExpectedCageCount, string? Note);

/// <summary>Create (POST) and update (PUT) of a plan, which spans the rafts of its lines whatever their zones (a <c>zoneId</c> sent is ignored); an update replaces the header, the orders and the lines.</summary>
/// <param name="Code">1-30 letters, digits and . _ -; empty = the next KHT on create, the stored one on update.</param>
/// <param name="PlannedAt">When the harvest is planned; any time.</param>
/// <param name="SalesOrderIds">The orders the plan serves (open ones may be newly linked); none is fine.</param>
/// <param name="RowVersion">Required on update.</param>
public sealed record SaveHarvestPlanRequest(
    string? Code, DateTimeOffset? PlannedAt, string? Note, IReadOnlyList<Guid>? SalesOrderIds, IReadOnlyList<SaveHarvestPlanLineRequest>? Lines, string? RowVersion);

/// <summary>Hand over (assign) or finish a plan; the row version is checked when sent.</summary>
public sealed record HarvestPlanActionRequest(string? RowVersion);

public sealed record CancelHarvestPlanRequest(string? Reason, string? RowVersion);

/// <summary>Lanes of a line a Zone Manager will not harvest ("Không thu"), or puts back.</summary>
/// <param name="LaneNumbers">Lanes of the line.</param>
/// <param name="Reason">Required when leaving lanes out (at most 500 characters); ignored when putting back.</param>
public sealed record SkipHarvestPlanLanesRequest(IReadOnlyList<int>? LaneNumbers, string? Reason);

public sealed record HarvestPlanSaved(HarvestPlanView Plan, string Message, string MessageCode);

/// <summary>The plans a Zone Manager (or the Owner) can record harvests from now: Assigned or Harvesting, in the zones the caller harvests.</summary>
public sealed record HarvestPlansToHarvest(IReadOnlyList<HarvestPlanView> Plans);

/// <summary>A product of the order with the cages worked out for the plan: the cages of a Lồng line, or the kg of the line over the kg of one Lồng of the product (rounded up); null when the product has no Lồng unit.</summary>
public sealed record HarvestPlanDraftProduct(
    Guid ProductId, string ProductCode, string ProductName, Guid UnitId, string UnitCode, string UnitName, decimal Quantity, int? ExpectedCageCount);

/// <summary>The form of a new plan made from an order: its pickup time as the planned time, and its products. The lines (rafts and lanes, of any zone) are for the Owner to choose.</summary>
public sealed record HarvestPlanOrderDraft(
    HarvestPlanOrderView SalesOrder, DateTimeOffset? PlannedAt, IReadOnlyList<HarvestPlanDraftProduct> Products, IReadOnlyList<SaveHarvestPlanLineRequest> Lines);

/// <summary>Where a lane stands for a new plan: free to plan, waiting in another open plan, or already harvested in the cycle.</summary>
public enum EligibleLaneState
{
    Free,
    InAnotherPlan,
    Harvested,
}

/// <param name="PlanCode">The open plan the lane waits in when <paramref name="State"/> is InAnotherPlan.</param>
public sealed record EligibleLaneView(Guid LaneId, int LaneNumber, EligibleLaneState State, string? PlanCode);

/// <summary>A raft with an Open cultivation cycle and at least one lane that can be harvested; <paramref name="Product"/> is what its cycle farms (null when it has not harvested with products yet).</summary>
public sealed record EligibleRaftView(Guid RaftId, string RaftCode, HarvestPlanZoneView Zone, HarvestPlanProductView? Product, IReadOnlyList<EligibleLaneView> Lanes);

/// <summary>The rafts a plan can be made for, by zone code and raft code.</summary>
public sealed record EligibleRafts(IReadOnlyList<EligibleRaftView> Rafts);
