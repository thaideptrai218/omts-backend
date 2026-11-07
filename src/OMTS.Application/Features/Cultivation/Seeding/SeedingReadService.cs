using OMTS.Application.Common;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Cultivation.Cycles;
using OMTS.Application.Features.Cultivation.Progress;
using OMTS.Application.Features.Purchasing;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Application.Features.Cultivation.Seeding;

/// <summary>CYCLE-02 and CYCLE-05 seeding reads, for a Zone Manager of the raft's zone and the Cooperative Owner; a closed cycle keeps its sessions.</summary>
public sealed class SeedingReadService(
    ICultivationCycleRepository cycles,
    ISeedingRecordRepository records,
    ISeedLotRepository lots,
    IRaftRepository rafts,
    IHarvestLotRepository harvestLots,
    LaneStateReader laneStates,
    SeedingViews views,
    ZoneAccessGuard zoneAccess,
    IStatusCatalog statusCatalog,
    TimeProvider timeProvider)
{
    /// <summary>CYCLE-02.0 step 3. With no lot to choose: MSG-CYCLE-03 (CYCLE-02.0.E1).</summary>
    public async Task<Result<SeedLotList>> SeedLotsAsync(Guid raftId, CancellationToken cancellationToken)
    {
        var raft = await rafts.GetForEditAsync(raftId, cancellationToken);
        if (raft is null)
        {
            return AppError.NotFound;
        }

        if (!await zoneAccess.AllowsAsync(PermissionFunctions.CultivationCycles, PermissionAction.Create, raft.ZoneId, cancellationToken))
        {
            return AppError.Forbidden;
        }

        var options = (await lots.ListOfferedAsync(raftId, cancellationToken)).Select(l => new SeedLotOption(
            l.LineId, DocumentCodes.SeedLotCode(l.ReceiptCode, l.LineNumber), l.ReceiptId, l.ReceiptCode, l.ReceivedDate, l.ItemId, l.ItemCode, l.ItemName, l.Unit, l.SupplierId, l.SupplierName,
            l.QuantityReceived, l.QuantitySeeded, l.RaftId == raftId)).ToList();
        var empty = options.Count == 0 ? UserMessages.NoSeedLot : null;
        return new SeedLotList(raftId, options, empty?.Text, empty?.Code);
    }

    public async Task<Result<SeedingList>> ListAsync(Guid cycleId, CancellationToken cancellationToken)
    {
        var cycle = await cycles.GetAsync(cycleId, cancellationToken);
        if (cycle is null)
        {
            return AppError.NotFound;
        }

        if (!await CanViewAsync(cycle.Raft.ZoneId, cancellationToken))
        {
            return AppError.Forbidden;
        }

        var raft = await rafts.GetForEditAsync(cycle.RaftId, cancellationToken) ?? cycle.Raft;
        var day = await laneStates.ReadAsync(raft, cycle.CloseDate ?? timeProvider.Today(), cancellationToken);
        var ofCycle = await records.ListByCycleAsync(cycle.Id, cancellationToken);
        var figures = SeedingViews.Figures(raft, day.States);
        var canRecord = CultivationCycleViews.CanEdit(cycle, cycle.Raft, cycle.Raft.Zone.StatusId == StatusIds.Usage.InUse)
            && await zoneAccess.AllowsAsync(PermissionFunctions.CultivationCycles, PermissionAction.Create, cycle.Raft.ZoneId, cancellationToken);
        var completed = cycle.SeedingCompletedOn is not null;
        return new SeedingList(
            cycle.Id,
            raft.Id,
            raft.Code,
            cycle.SeedingCompletedOn,
            figures.LaneCount,
            figures.SeededCount,
            figures.NotSeededLanes,
            canRecord,
            canRecord && !completed,
            raft.Lanes
                .Where(l => l.StatusId == StatusIds.Lane.InUse || day.LanesWithRecords.Contains(l.Id))
                .OrderBy(l => l.LaneNumber)
                .Select(l => new SeedingLaneView(
                    l.Id, l.LaneNumber, l.StatusId, statusCatalog.DisplayNameOf(l.StatusId), day.States[l.Id],
                    canRecord && l.StatusId == StatusIds.Lane.InUse && SeedingLaneRules.CanBeSeeded(day.States[l.Id], completed)))
                .ToList(),
            await views.ListAsync(ofCycle, cycle, day.States, cancellationToken),
            ofCycle.GroupBy(r => r.SeedLotLineId)
                .Select(g => new SeedLotTotal(g.Key, SeedingValues.LotCode(g.First().SeedLotLine), g.First().SeedLotLine.Item?.Name, g.First().SeedLotLine.Unit, g.Sum(r => r.Quantity)))
                .OrderBy(t => t.Code, StringComparer.Ordinal)
                .ToList(),
            RowVersionGuard.Encode(cycle.RowVersion));
    }

    public async Task<Result<SeedingRecordView>> GetAsync(Guid recordId, CancellationToken cancellationToken)
    {
        var record = await records.GetAsync(recordId, cancellationToken);
        if (record is null)
        {
            return AppError.NotFound;
        }

        if (!await CanViewAsync(record.Cycle.Raft.ZoneId, cancellationToken))
        {
            return AppError.Forbidden;
        }

        var raft = await rafts.GetForEditAsync(record.Cycle.RaftId, cancellationToken) ?? record.Cycle.Raft;
        var day = await laneStates.ReadAsync(raft, record.Cycle.CloseDate ?? timeProvider.Today(), cancellationToken);
        return await views.ViewAsync(record, day.States, cancellationToken);
    }

    /// <summary>CYCLE-05.0 step 3: without a cycle, the Open cycle of the raft, else the one started last.</summary>
    public async Task<Result<LaneProgressView>> LaneProgressAsync(Guid raftId, int laneNumber, Guid? cycleId, CancellationToken cancellationToken)
    {
        var raft = await rafts.GetForEditAsync(raftId, cancellationToken);
        var lane = raft?.Lanes.FirstOrDefault(l => l.LaneNumber == laneNumber);
        if (raft is null || lane is null)
        {
            return AppError.NotFound;
        }

        if (!await CanViewAsync(raft.ZoneId, cancellationToken))
        {
            return AppError.Forbidden;
        }

        var cycle = await CycleShownAsync(raft, cycleId, cancellationToken);
        if (cycleId is not null && cycle is null)
        {
            return AppError.NotFound;
        }

        var state = cycle is null ? LaneState.NotSeeded : (await laneStates.ReadAsync(raft, cycle.CloseDate ?? timeProvider.Today(), cancellationToken)).States[lane.Id];
        var sessions = cycle is null ? [] : (await records.ListByCycleAsync(cycle.Id, cancellationToken))
            .Where(r => r.Lanes.Any(l => l.LaneId == lane.Id))
            .Select(r => new LaneSeedingSession(r.Id, r.SeedingDate, SeedingViews.LotRef(r.SeedLotLine), r.Quantity))
            .ToList();
        var lots = cycle is null ? [] : (await harvestLots.ListOfLaneAsync(cycle.Id, lane.Id, cancellationToken))
            .Select(l => new LaneHarvestLot(l.LotId, l.Lot.LotCode, l.Lot.HarvestDate, l.IsFinished, l.Lot.StatusId, statusCatalog.DisplayNameOf(l.Lot.StatusId)))
            .ToList();
        return new LaneProgressView(
            raft.Id, raft.Code, lane.Id, lane.LaneNumber, lane.StatusId, statusCatalog.DisplayNameOf(lane.StatusId), state,
            cycle is null ? null : new CycleListItem(cycle.Id, cycle.StartDate, cycle.CloseDate, cycle.StatusId, statusCatalog.DisplayNameOf(cycle.StatusId), RowVersionGuard.Encode(cycle.RowVersion), cycle.ClosedEarly),
            sessions,
            lots);
    }

    /// <summary>CYCLE-05.0 step 1: for each seeded lane on the date, its seed lots and seeding dates.</summary>
    public async Task<IReadOnlyList<LaneSeedLots>> LaneSeedLotsAsync(Guid cycleId, DateOnly date, CancellationToken cancellationToken)
    {
        var hung = (await records.ListByCycleAsync(cycleId, cancellationToken))
            .Where(r => r.SeedingDate <= date)
            .SelectMany(r => r.Lanes.Select(l => (l.Lane.LaneNumber, Record: r)));
        return hung.GroupBy(h => h.LaneNumber).OrderBy(g => g.Key).Select(lane => new LaneSeedLots(
            lane.Key,
            lane.GroupBy(h => h.Record.SeedLotLineId).Select(lot =>
            {
                var line = lot.First().Record.SeedLotLine;
                return new LaneSeedLot(lot.Key, SeedingValues.LotCode(line), line.Item?.Name, lot.Select(h => h.Record.SeedingDate).Distinct().Order().ToList());
            }).OrderBy(l => l.Code, StringComparer.Ordinal).ToList())).ToList();
    }

    private async Task<CultivationCycle?> CycleShownAsync(Raft raft, Guid? cycleId, CancellationToken cancellationToken)
    {
        if (cycleId is { } id)
        {
            var chosen = await cycles.GetAsync(id, cancellationToken);
            return chosen?.RaftId == raft.Id ? chosen : null;
        }

        var ofRaft = await cycles.ListByRaftAsync(raft.Id, cancellationToken);
        return ofRaft.FirstOrDefault(c => c.StatusId == StatusIds.CultivationCycle.Open) ?? ofRaft.FirstOrDefault();
    }

    private Task<bool> CanViewAsync(Guid zoneId, CancellationToken cancellationToken) =>
        zoneAccess.AllowsAsync(PermissionFunctions.CultivationCycles, PermissionAction.View, zoneId, cancellationToken);
}
