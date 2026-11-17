using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Cultivation.CultivationLogs;
using OMTS.Application.Features.Cultivation.Cycles;
using OMTS.Application.Features.Cultivation.Seeding;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Harvest;

namespace OMTS.Application.Features.Traceability.Corrections;

/// <summary>SCR-TRACE-02 (TRACE-03.0 steps 1 to 3): the cycles of a raft and the records of a cycle that can be corrected only here (BR-TRACE-04). A Discontinued raft keeps its history.</summary>
public sealed class TraceCorrectionReadService(
    IRaftRepository rafts,
    ICultivationCycleRepository cycles,
    ISeedingRecordRepository seedingRecords,
    IHarvestLotRepository harvestLots,
    CultivationCycleViews cycleViews,
    CultivationLogViews logViews,
    TraceCorrectionGuard guard,
    IStatusCatalog statusCatalog)
{
    /// <summary>Step 1: the cycles of the raft, Open and Closed, the newest first.</summary>
    public async Task<Result<TraceCorrectionCycles>> CyclesOfRaftAsync(Guid raftId, CancellationToken cancellationToken)
    {
        var raft = await rafts.GetForEditAsync(raftId, cancellationToken);
        if (raft is null)
        {
            return AppError.NotFound;
        }

        if (!await guard.MayCorrectAsync(raft.ZoneId, cancellationToken))
        {
            return AppError.Forbidden;
        }

        var ofRaft = await cycles.ListByRaftAsync(raftId, cancellationToken);
        return new TraceCorrectionCycles(raft.Id, raft.Code, ofRaft.Select(c => cycleViews.ListItem(c)).ToList(), !await guard.IsOwnerAsync(cancellationToken));
    }

    /// <summary>Step 3, or MSG-GEN-10 with empty lists when the cycle has no such record (TRACE-03.0.E1).</summary>
    public async Task<Result<TraceCorrectionRecords>> RecordsOfCycleAsync(Guid cycleId, CancellationToken cancellationToken)
    {
        var cycle = await cycles.GetForEditAsync(cycleId, cancellationToken);
        if (cycle is null)
        {
            return AppError.NotFound;
        }

        if (!await guard.MayCorrectAsync(cycle.Raft.ZoneId, cancellationToken))
        {
            return AppError.Forbidden;
        }

        var closed = cycle.StatusId == StatusIds.CultivationCycle.Closed;
        var lots = await harvestLots.ListByCycleAsync(cycle.Id, cancellationToken);
        var sessions = (await seedingRecords.ListByCycleAsync(cycle.Id, cancellationToken))
            .Select(s => (Session: s, Lots: TraceSeedLotRules.LotsHolding(lots, s.Lanes.Select(l => l.LaneId).ToHashSet())))
            .Where(s => closed || s.Lots.Count > 0)
            .Select(s => new TraceSeedingItem(
                s.Session.Id, s.Session.SeedingDate, SeedingViews.LotRef(s.Session.SeedLotLine), s.Session.Quantity,
                s.Session.Lanes.Select(l => l.Lane.LaneNumber).Order().ToList(), TraceCorrectionAnswers.Codes(s.Lots), RowVersionGuard.Encode(s.Session.RowVersion)))
            .ToList();
        var logs = closed ? await logViews.ListAsync(cycle, cancellationToken) : [];
        var lotItems = closed ? lots.OrderBy(l => l.HarvestedAt).Select(LotItem).ToList() : [];
        var empty = sessions.Count == 0 && logs.Count == 0 && lotItems.Count == 0 ? UserMessages.NoDataForFilters("cycle") : null;
        return new TraceCorrectionRecords(
            cycle.Id, cycle.RaftId, cycle.Raft.Code, cycle.StatusId, statusCatalog.DisplayNameOf(cycle.StatusId), cycle.StartDate, cycle.CloseDate,
            sessions, logs, lotItems, !await guard.IsOwnerAsync(cancellationToken), empty?.Text, empty?.Code);
    }

    private TraceHarvestLotItem LotItem(HarvestLot lot) => new(
        lot.Id,
        lot.LotCode,
        lot.HarvestedAt,
        lot.HarvestDate,
        lot.Lanes.Select(l => l.Lane.LaneNumber).Order().ToList(),
        lot.Lanes.Where(l => l.IsFinished).Select(l => l.Lane.LaneNumber).Order().ToList(),
        lot.CageCount,
        lot.KgPerCage,
        lot.EstimatedKg,
        lot.StatusId,
        statusCatalog.DisplayNameOf(lot.StatusId),
        lot.OriginState,
        RowVersionGuard.Encode(lot.RowVersion));
}
