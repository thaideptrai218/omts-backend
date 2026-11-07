using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Cultivation.Cycles;
using OMTS.Application.Features.Cultivation.Progress;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Harvest;

namespace OMTS.Application.Features.Harvest;

/// <summary>What a correction and a removal of a lot both ask first (HARVEST-01.1, BR-HARVEST-05): may this caller change it, and how do the lanes stand.</summary>
public sealed class HarvestLotEditReader(
    IHarvestLotRepository lots, IRaftRepository rafts, ISeedingRecordRepository seedingRecords, CycleWriteGuard writes, HarvestLotViews views, TimeProvider timeProvider)
{
    /// <summary>MSG-GEN-08 when the caller may not change this lot, MSG-GEN-09 when its cycle is Closed or its raft or zone is read-only. The lot comes tracked, with its cycle, raft and zone.</summary>
    internal async Task<AppError?> RefusalAsync(HarvestLot lot, CancellationToken cancellationToken) =>
        HarvestLotViews.MayCorrect(await views.AccessAsync(cancellationToken), lot, lot.Raft.ZoneId) ? writes.Refusal(lot.Cycle) : AppError.Forbidden;

    /// <summary>The raft with its lanes, and the lanes as the lot sees them.</summary>
    /// <param name="asOf">The day the lanes are read on: today for an Open cycle; TRACE-03.2 reads a Closed cycle on its close date, as its views do.</param>
    internal async Task<(Raft Raft, HarvestLotLaneFacts Facts)> FactsAsync(HarvestLot lot, CancellationToken cancellationToken, DateOnly? asOf = null)
    {
        var raft = await rafts.GetForEditAsync(lot.RaftId, cancellationToken) ?? lot.Raft;
        var cycle = lot.Cycle;
        var facts = new HarvestLotLaneFacts(
            raft,
            new CycleSpan(cycle.Id, cycle.RaftId, cycle.StartDate, cycle.CloseDate),
            lot,
            await seedingRecords.LaneSeedingsAsync(cycle.Id, cancellationToken),
            await lots.LaneFactsAsync(cycle.Id, cancellationToken),
            asOf ?? timeProvider.Today());
        return (raft, facts);
    }
}
