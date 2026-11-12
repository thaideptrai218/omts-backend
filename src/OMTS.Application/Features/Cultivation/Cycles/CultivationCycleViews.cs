using OMTS.Application.Common;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Interfaces.Storage;
using OMTS.Application.Features.Cultivation.Nursery;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Application.Features.Cultivation.Cycles;

/// <summary>The cycle answers, with the figures and commands worked out when shown.</summary>
public sealed class CultivationCycleViews(
    ICultivationCycleRepository cycles, IHarvestLotRepository harvestLots, INurseryBatchRepository nurseries, IFileStorage storage, IStatusCatalog statusCatalog, TimeProvider timeProvider)
{
    /// <summary>The cycle with its raft and zone loaded, for one cycle shown at a time: the list items hold no days of farming. An Open cycle counts to today, so only a closed one reads its last lot.</summary>
    public async Task<CycleView> ViewAsync(CultivationCycle cycle, CancellationToken cancellationToken)
    {
        var hasRecords = (await cycles.WithRecordsAsync([cycle.Id], cancellationToken)).Count > 0;
        var lastLotDate = cycle.CloseDate is null ? null : await harvestLots.LastHarvestDateAsync(cycle.Id, cancellationToken);
        var zoneInUse = cycle.Raft.Zone.StatusId == StatusIds.Usage.InUse;
        var nursery = cycle.NurseryBatchId is null ? null : (await nurseries.FactsByCycleAsync([cycle.Id], cancellationToken)).GetValueOrDefault(cycle.Id);
        // A cycle from a nursery batch shows the completion photos of its batch; any other shows its own plan photos (decision of 12/11/2026).
        var photos = new List<CyclePlanPhotoView>();
        var rows = cycle.NurseryBatchId is { } batchId
            ? (await nurseries.CompletionPhotosAsync(batchId, cancellationToken)).Select(p => (p.Id, p.SortOrder, p.FileKey)).ToList()
            : (await cycles.PlanPhotosAsync(cycle.Id, cancellationToken)).Select(p => (p.Id, p.SortOrder, p.FileKey)).ToList();
        foreach (var (id, sortOrder, key) in rows)
        {
            photos.Add(new CyclePlanPhotoView(id, sortOrder, await storage.GetReadUrlAsync(key, cancellationToken: cancellationToken)));
        }

        return new CycleView(
            cycle.Id,
            cycle.RaftId,
            cycle.Raft.Code,
            cycle.StartDate,
            cycle.CloseDate,
            cycle.ExpectedSeedQuantity,
            cycle.TargetYieldKg,
            cycle.SeedingCompletedOn,
            cycle.Assessment,
            cycle.ClosedEarly,
            CycleDaysOfFarming.Count(cycle.StartDate, lastLotDate, cycle.CloseDate, timeProvider.Today()),
            cycle.StatusId,
            statusCatalog.DisplayNameOf(cycle.StatusId),
            CanEdit(cycle, cycle.Raft, zoneInUse),
            CanCancel(cycle, cycle.Raft, zoneInUse, hasRecords),
            RowVersionGuard.Encode(cycle.RowVersion),
            cycle.NurseryBatchId,
            nursery?.BatchCode,
            nursery?.Allocated,
            nursery?.Seeded,
            nursery is null ? null : NurseryRules.Remaining(nursery.Allocated, nursery.Seeded),
            photos,
            cycle.NurseryBatchId is not null);
    }

    public CycleListItem ListItem(CultivationCycle cycle) => new(
        cycle.Id, cycle.StartDate, cycle.CloseDate, cycle.StatusId, statusCatalog.DisplayNameOf(cycle.StatusId), RowVersionGuard.Encode(cycle.RowVersion), cycle.ClosedEarly, cycle.NurseryBatchId);

    /// <summary>CYCLE-01 PRE-3, as the writes answer it: an Open cycle, with neither its raft nor its zone Discontinued.</summary>
    public static bool CanEdit(CultivationCycle? cycle, Raft raft, bool zoneInUse) =>
        cycle?.StatusId == StatusIds.CultivationCycle.Open && raft.StatusId == StatusIds.Usage.InUse && zoneInUse;

    /// <summary>CYCLE-01 PRE-3.</summary>
    public static bool CanCancel(CultivationCycle? cycle, Raft raft, bool zoneInUse, bool hasRecords) => CanEdit(cycle, raft, zoneInUse) && !hasRecords;
}
