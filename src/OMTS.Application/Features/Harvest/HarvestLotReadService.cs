using OMTS.Application.Common;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Cultivation.Cycles;
using OMTS.Application.Features.Cultivation.Progress;
using OMTS.Application.Features.Sales.PurchaseRequests;
using OMTS.Domain.Constants;

namespace OMTS.Application.Features.Harvest;

/// <summary>HARVEST-01 reads, for a Zone Manager of the raft's zone and the Cooperative Owner. Viewing changes nothing.</summary>
public sealed class HarvestLotReadService(
    IRaftRepository rafts,
    ICultivationCycleRepository cycles,
    ICultivationCycleQuery cycleQuery,
    CycleSeedLabels seedLabels,
    IHarvestLotRepository lots,
    ISeedingRecordRepository seedingRecords,
    IPurchaseRequestRepository requests,
    IPurchaseRequestFulfilment fulfilment,
    ICooperativeRepository cooperatives,
    LaneStateReader laneStates,
    LaneProgressViews laneProgress,
    HarvestLotViews views,
    ZoneAccessGuard zoneAccess,
    IStatusCatalog statusCatalog,
    TimeProvider timeProvider)
{
    /// <summary>HARVEST-01.0 step 1: the lanes with their state today and the lots of the Open cycle. With no Open cycle: the lanes, no lot, and nothing to record.</summary>
    public async Task<Result<HarvestLotsScreen>> ScreenAsync(Guid raftId, CancellationToken cancellationToken)
    {
        var raft = await rafts.GetForEditAsync(raftId, cancellationToken);
        if (raft is null)
        {
            return AppError.NotFound;
        }

        if (!await zoneAccess.AllowsAsync(PermissionFunctions.Harvests, PermissionAction.View, raft.ZoneId, cancellationToken))
        {
            return AppError.Forbidden;
        }

        var day = await laneStates.ReadAsync(raft, timeProvider.Today(), cancellationToken);
        var span = await cycleQuery.OpenCycleAsync(raft.Id, cancellationToken);
        var open = span is null ? null : await cycles.GetAsync(span.Id, cancellationToken);
        var raftIsEditable = raft.StatusId == StatusIds.Usage.InUse && raft.Zone.StatusId == StatusIds.Usage.InUse;
        var mayCreate = await zoneAccess.AllowsAsync(PermissionFunctions.Harvests, PermissionAction.Create, raft.ZoneId, cancellationToken);
        var mayRecord = open is not null && raftIsEditable && mayCreate;
        var ofCycle = open is null ? [] : await lots.ListByCycleAsync(open.Id, cancellationToken);
        var seedLotCodes = open is null ? new Dictionary<Guid, IReadOnlyList<string>>() : SeedLotCodesByLane(await seedingRecords.ListByCycleAsync(open.Id, cancellationToken));
        var progress = await laneProgress.InfoAsync(day, cancellationToken);
        var lanes = raft.Lanes
            .Where(l => l.StatusId == StatusIds.Lane.InUse || day.LanesWithRecords.Contains(l.Id))
            .OrderBy(l => l.LaneNumber)
            .Select(l =>
            {
                var state = day.States[l.Id];
                var canHarvest = mayRecord && l.IsUsable && LaneStateRules.CanBeHarvested(state);
                var ofLane = ofCycle.Where(lot => lot.Lanes.Any(x => x.LaneId == l.Id)).ToList();
                var (reasonCode, reason) = canHarvest ? ((string?)null, (string?)null) : Refusal(l, state, open is not null, raftIsEditable, mayCreate);
                return new HarvestLaneView(
                    l.Id, l.LaneNumber, l.StatusId, statusCatalog.DisplayNameOf(l.StatusId), state, canHarvest, reason, reasonCode,
                    mayRecord && l.StatusId == StatusIds.Lane.InUse && state == LaneState.BeingHarvested,
                    ofLane.Count,
                    ofLane.Count == 0 ? null : ofLane.Max(lot => lot.HarvestDate),
                    seedLotCodes.GetValueOrDefault(l.Id, []),
                    ofLane.Sum(lot => lot.CageCount / lot.Lanes.Count),
                    l.IsDamaged,
                    l.IsOutOfService,
                    Farming.Rafts.RaftGeometry.EffectiveLineLength(l, raft),
                    progress[l.Id].SeededPercent, progress[l.Id].HarvestedPercent, progress[l.Id].LastModifiedByName, progress[l.Id].LastModifiedAt, progress[l.Id].ProgressHistory);
            })
            .ToList();
        var cooperative = await cooperatives.GetCurrentAsync(cancellationToken) ?? throw new InvalidOperationException("A harvest lot needs the cooperative created at deployment.");
        var harvestedKg = ofCycle.Sum(lot => lot.EstimatedKg);

        return new HarvestLotsScreen(
            raft.Id,
            raft.Code,
            open is null ? null : new CycleListItem(open.Id, open.StartDate, open.CloseDate, open.StatusId, statusCatalog.DisplayNameOf(open.StatusId), RowVersionGuard.Encode(open.RowVersion), open.ClosedEarly, open.NurseryBatchId)
                .WithSeed((await seedLabels.ReadAsync([open.Id], cancellationToken)).GetValueOrDefault(open.Id)),
            cooperative.AverageKgPerCage,
            lanes.Any(l => l.CanHarvest),
            lanes,
            open is null ? [] : await views.ListAsync(ofCycle, open, raft, cancellationToken),
            mayRecord ? await OrdersAsync(raft.Id, open?.ProductId, cancellationToken) : [],
            ofCycle.Sum(lot => lot.CageCount),
            harvestedKg,
            open?.TargetYieldKg,
            open?.TargetYieldKg is { } target ? target - harvestedKg : null,
            HarvestQuantityRules.TargetPercent(open?.TargetYieldKg, harvestedKg),
            open?.TargetYieldKg is > 0 and var over && harvestedKg > over,
            open?.ProductId,
            open?.Product?.Name);
    }

    /// <summary>
    /// What a new lot on the raft can be recorded for (decision of 11/11/2026): the requests planned on this raft first, then the earliest agreed pickup. A request taken in
    /// full before that decision is left out, and so is one whose lots already reach its accepted cages (15/15, decision of 13/11/2026).
    /// </summary>
    private async Task<IReadOnlyList<HarvestOrderOption>> OrdersAsync(Guid raftId, Guid? cycleProductId, CancellationToken cancellationToken)
    {
        var accepted = await requests.ListOpenForHarvestAsync(cancellationToken);
        var fulfilled = await fulfilment.FulfilledAmongAsync(accepted.Select(r => r.Id).ToList(), cancellationToken);
        var open = accepted.Where(r => !fulfilled.Contains(r.Id)).ToList();
        var harvested = await lots.CagesOfPurchaseRequestsAsync(open.Select(r => r.Id).ToList(), cancellationToken);
        // Not limited to this raft or zone: a request is gathered from several rafts, so every lot of it counts and shows where it came from.
        var shown = open.Where(r => !HarvestOrderCages.Reached(r.AcceptedCages, harvested.GetValueOrDefault(r.Id))).ToList();
        var options = new List<HarvestOrderOption>();
        foreach (var r in shown)
        {
            var done = harvested.GetValueOrDefault(r.Id);
            var trips = done > 0 ? TripsOf(await lots.ListOfPurchaseRequestAsync(r.Id, cancellationToken)) : [];
            options.Add(new HarvestOrderOption(
                r.Id, r.Code, r.Customer.Name, r.ProductName, r.AcceptedCages ?? 0, done, r.AgreedPickupDate ?? r.PickupDate,
                r.WorkDayPlan?.WorkDate ?? r.WorkDate, r.WorkDayPlan?.RaftId == raftId, r.ProductId, HarvestProductMatch.Matches(cycleProductId, r.ProductId), trips));
        }

        return options.OrderByDescending(o => o.PlannedOnRaft).ToList();
    }

    /// <summary>The lots of a request grouped per raft, the raft of the latest lot first, at most 10.</summary>
    private static IReadOnlyList<HarvestOrderTrip> TripsOf(IReadOnlyList<Domain.Entities.Harvest.HarvestLot> ofRequest) => ofRequest
        .GroupBy(l => l.RaftId)
        .Select(g => new HarvestOrderTrip(g.First().Raft.Code, g.First().Raft.Zone.Code, g.Sum(l => l.CageCount), g.Max(l => l.HarvestDate)))
        .OrderByDescending(t => t.RecordedOn)
        .Take(10)
        .ToList();

    /// <summary>Why a lane cannot be chosen for a harvest now, as a code for the client and a sentence.</summary>
    private static (string Code, string Reason) Refusal(Domain.Entities.Farming.Lane lane, LaneState state, bool hasOpenCycle, bool raftIsEditable, bool mayCreate) =>
        lane.StatusId != StatusIds.Lane.InUse ? ("LaneRemoved", "The lane is removed.")
        : Farming.Rafts.LaneReasonCodes.Of(lane) is { } flag ? (flag, Farming.Rafts.LaneReasonCodes.TextOf(flag))
        : !hasOpenCycle ? ("NoOpenCycle", "The raft has no Open cycle.")
        : !raftIsEditable ? ("ReadOnly", "The raft or its zone is not In Use.")
        : !mayCreate ? ("NoPermission", "You may not record a harvest on this raft.")
        : state == LaneState.Harvested ? ("AlreadyHarvested", "The lane is already harvested.")
        : ("NotSeeded", "The lane has no seed yet.");

    /// <summary>The seed lot codes (receipt code and line) hung on each lane in the sessions of the cycle.</summary>
    private static Dictionary<Guid, IReadOnlyList<string>> SeedLotCodesByLane(IReadOnlyList<Domain.Entities.Cultivation.SeedingRecord> sessions) => sessions
        .SelectMany(s => s.Lanes.Select(l => (l.LaneId, Code: Cultivation.Seeding.SeedingValues.LotCode(s.SeedLotLine))))
        .GroupBy(x => x.LaneId)
        .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(x => x.Code).Distinct().Order(StringComparer.Ordinal).ToList());

    public async Task<Result<HarvestLotView>> GetAsync(Guid lotId, CancellationToken cancellationToken)
    {
        var lot = await lots.GetAsync(lotId, cancellationToken);
        if (lot is null)
        {
            return AppError.NotFound;
        }

        return await zoneAccess.AllowsAsync(PermissionFunctions.Harvests, PermissionAction.View, lot.Raft.ZoneId, cancellationToken)
            ? await views.ViewAsync(lot, lot.Raft, cancellationToken)
            : AppError.Forbidden;
    }
}
