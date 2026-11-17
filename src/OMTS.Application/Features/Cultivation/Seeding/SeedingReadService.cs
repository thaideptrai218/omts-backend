using OMTS.Application.Common;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Cultivation.Cycles;
using OMTS.Application.Features.Cultivation.Nursery;
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
    INurseryBatchRepository nurseries,
    CycleSeedLabels seedLabels,
    IRaftRepository rafts,
    IHarvestLotRepository harvestLots,
    LaneStateReader laneStates,
    LaneProgressViews laneProgress,
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
            l.QuantityReceived, l.QuantitySeeded, l.RaftId == raftId, l.QuantityReturned, l.QuantityConsumed)).ToList();
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
        var batch = cycle.NurseryBatchId is { } batchId ? await nurseries.GetForPoolAsync(batchId, cancellationToken) : null;
        var poolSeeded = batch is null ? 0m : (await nurseries.SeededByBatchAsync([batch.Id], null, cancellationToken)).GetValueOrDefault(batch.Id);
        // A cycle started the old way farms what its sessions hung: the lot with the most seed is named, the other item names listed (owner feedback 13/11/2026).
        var legacyLabel = batch is null ? CycleSeedLabels.Build(CycleSeedLabels.FromRecords(ofCycle)) : null;
        var legacySeed = legacyLabel?.PrimaryLotLineId is { } lineId ? (Lot: SeedingViews.LotRef(ofCycle.First(r => r.SeedLotLineId == lineId).SeedLotLine), Label: legacyLabel) : ((SeedLotRef Lot, CycleSeedLabel Label)?)null;
        var completed = cycle.SeedingCompletedOn is not null;
        // A cycle that draws from a nursery takes no new session once its seeding is marked complete (v2).
        var canRecord = CultivationCycleViews.CanEdit(cycle, cycle.Raft, cycle.Raft.Zone.StatusId == StatusIds.Usage.InUse)
            && !(batch is not null && completed)
            && await zoneAccess.AllowsAsync(PermissionFunctions.CultivationCycles, PermissionAction.Create, cycle.Raft.ZoneId, cancellationToken);
        var progress = await laneProgress.InfoAsync(day, cancellationToken);
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
                    canRecord && l.IsUsable && SeedingLaneRules.CanBeSeeded(day.States[l.Id], completed),
                    l.IsDamaged, l.IsOutOfService, Farming.Rafts.LaneReasonCodes.Of(l),
                    progress[l.Id].SeededPercent, progress[l.Id].HarvestedPercent, progress[l.Id].LastModifiedByName, progress[l.Id].LastModifiedAt, progress[l.Id].ProgressHistory,
                    Math.Max(0m, 100m - progress[l.Id].SeededPercent)))
                .ToList(),
            await views.ListAsync(ofCycle, cycle, day.States, cancellationToken),
            ofCycle.GroupBy(r => r.SeedLotLineId)
                .Select(g => new SeedLotTotal(g.Key, SeedingValues.LotCode(g.First().SeedLotLine), g.First().SeedLotLine.Item?.Name, g.First().SeedLotLine.Item?.Unit ?? g.First().SeedLotLine.Unit, g.Sum(r => r.Quantity)))
                .OrderBy(t => t.Code, StringComparer.Ordinal)
                .ToList(),
            RowVersionGuard.Encode(cycle.RowVersion),
            cycle.ExpectedSeedQuantity,
            ofCycle.Sum(r => r.Quantity),
            SeedingPlanRules.Percent(cycle.ExpectedSeedQuantity, ofCycle.Sum(r => r.Quantity)),
            cycle.NurseryBatchId,
            batch?.Code,
            batch is null ? null : ofCycle.Sum(r => r.Quantity),
            batch is null ? null : NurseryRules.PoolRemaining(batch, poolSeeded) ?? 0m,
            batch is null ? legacySeed?.Lot : SeedingViews.LotRef(batch.SeedLotLine),
            raft.Lanes
                .Where(l => l.StatusId == StatusIds.Lane.InUse && l.IsUsable && SeedingLaneRules.CanBeSeeded(day.States[l.Id], completed) && progress[l.Id].SeededPercent < 100m)
                .OrderBy(l => l.LaneNumber).Select(l => l.LaneNumber).ToList(),
            batch is null ? legacySeed?.Label.SeedItemName : batch.SeedLotLine.Item?.Name,
            legacySeed?.Label.SeedItemNames);
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

        var shownDay = cycle is null ? null : await laneStates.ReadAsync(raft, cycle.CloseDate ?? timeProvider.Today(), cancellationToken);
        var state = shownDay?.States[lane.Id] ?? LaneState.NotSeeded;
        var info = shownDay is null ? null : (await laneProgress.InfoAsync(shownDay, cancellationToken))[lane.Id];
        var sessions = cycle is null ? [] : (await records.ListByCycleAsync(cycle.Id, cancellationToken))
            .Where(r => r.Lanes.Any(l => l.LaneId == lane.Id))
            .Select(r => new LaneSeedingSession(r.Id, r.SeedingDate, SeedingViews.LotRef(r.SeedLotLine), r.Quantity))
            .ToList();
        var lots = cycle is null ? [] : (await harvestLots.ListOfLaneAsync(cycle.Id, lane.Id, cancellationToken))
            .Select(l => new LaneHarvestLot(l.LotId, l.Lot.LotCode, l.Lot.HarvestDate, l.IsFinished, l.Lot.StatusId, statusCatalog.DisplayNameOf(l.Lot.StatusId)))
            .ToList();
        return new LaneProgressView(
            raft.Id, raft.Code, lane.Id, lane.LaneNumber, lane.StatusId, statusCatalog.DisplayNameOf(lane.StatusId), state,
            cycle is null ? null : new CycleListItem(cycle.Id, cycle.StartDate, cycle.CloseDate, cycle.StatusId, statusCatalog.DisplayNameOf(cycle.StatusId), RowVersionGuard.Encode(cycle.RowVersion), cycle.ClosedEarly, cycle.NurseryBatchId)
                .WithSeed((await seedLabels.ReadAsync([cycle.Id], cancellationToken)).GetValueOrDefault(cycle.Id)),
            sessions,
            lots,
            info?.SeededPercent ?? 0m, info?.HarvestedPercent, info?.LastModifiedByName, info?.LastModifiedAt, info?.ProgressHistory);
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

