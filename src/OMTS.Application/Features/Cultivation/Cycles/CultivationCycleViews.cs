using OMTS.Application.Common;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Interfaces.Storage;
using OMTS.Application.Features.Cultivation.Nursery;
using OMTS.Application.Features.Cultivation.Seeding;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Application.Features.Cultivation.Cycles;

/// <summary>The cycle answers, with the figures and commands worked out when shown.</summary>
public sealed class CultivationCycleViews(
    ICultivationCycleRepository cycles, IHarvestLotRepository harvestLots, INurseryBatchRepository nurseries, IProductRepository products, IFileStorage storage, IStatusCatalog statusCatalog, TimeProvider timeProvider)
{
    /// <summary>The cycle with its raft and zone loaded, for one cycle shown at a time: the list items hold no days of farming. An Open cycle counts to today, so only a closed one reads its last lot.</summary>
    public async Task<CycleView> ViewAsync(CultivationCycle cycle, CancellationToken cancellationToken)
    {
        var hasRecords = (await cycles.WithRecordsAsync([cycle.Id], cancellationToken)).Count > 0;
        var lastLotDate = cycle.CloseDate is null ? null : await harvestLots.LastHarvestDateAsync(cycle.Id, cancellationToken);
        var zoneInUse = cycle.Raft.Zone.StatusId == StatusIds.Usage.InUse;
        var batch = cycle.NurseryBatchId is { } batchId ? await nurseries.GetForPoolAsync(batchId, cancellationToken) : null;
        var seededByCycle = (await nurseries.SeededByCycleAsync([cycle.Id], cancellationToken)).GetValueOrDefault(cycle.Id);
        var poolSeeded = batch is null ? 0m : (await nurseries.SeededByBatchAsync([batch.Id], null, cancellationToken)).GetValueOrDefault(batch.Id);
        // A cycle shows its own plan photos (v2); an older cycle from a nursery that has none shows the completion photos of its batch (decision of 12/11/2026).
        var own = await cycles.PlanPhotosAsync(cycle.Id, cancellationToken);
        var fromNursery = own.Count == 0 && cycle.NurseryBatchId is not null;
        var rows = fromNursery
            ? (await nurseries.CompletionPhotosAsync(cycle.NurseryBatchId!.Value, cancellationToken)).Select(p => (p.Id, p.SortOrder, p.FileKey)).ToList()
            : own.Select(p => (p.Id, p.SortOrder, p.FileKey)).ToList();
        var photos = new List<CyclePlanPhotoView>();
        foreach (var (id, sortOrder, key) in rows)
        {
            photos.Add(new CyclePlanPhotoView(id, sortOrder, await storage.GetReadUrlAsync(key, cancellationToken: cancellationToken)));
        }

        // Most loads include the product; one that did not is read by id.
        var productName = cycle.ProductId is not { } productId ? null : cycle.Product?.Name ?? (await products.GetForEditAsync(productId, cancellationToken))?.Name;
        var canEdit = CanEdit(cycle, cycle.Raft, zoneInUse);
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
            canEdit,
            CanCancel(cycle, cycle.Raft, zoneInUse, hasRecords),
            RowVersionGuard.Encode(cycle.RowVersion),
            cycle.NurseryBatchId,
            batch?.Code,
            batch is null ? null : seededByCycle,
            batch is null ? null : NurseryRules.PoolRemaining(batch, poolSeeded) ?? 0m,
            photos,
            fromNursery,
            cycle.Note,
            batch is null ? null : SeedingViews.LotRef(batch.SeedLotLine),
            canEdit && seededByCycle <= 0m,
            cycle.ProductId,
            productName,
            canEdit && seededByCycle <= 0m,
            batch?.SeedLotLine.Item?.Name);
    }
    public CycleListItem ListItem(CultivationCycle cycle) => new(
        cycle.Id, cycle.StartDate, cycle.CloseDate, cycle.StatusId, statusCatalog.DisplayNameOf(cycle.StatusId), RowVersionGuard.Encode(cycle.RowVersion), cycle.ClosedEarly, cycle.NurseryBatchId);

    /// <summary>CYCLE-01 PRE-3, as the writes answer it: an Open cycle, with neither its raft nor its zone Discontinued.</summary>
    public static bool CanEdit(CultivationCycle? cycle, Raft raft, bool zoneInUse) =>
        cycle?.StatusId == StatusIds.CultivationCycle.Open && raft.StatusId == StatusIds.Usage.InUse && zoneInUse;

    /// <summary>CYCLE-01 PRE-3.</summary>
    public static bool CanCancel(CultivationCycle? cycle, Raft raft, bool zoneInUse, bool hasRecords) => CanEdit(cycle, raft, zoneInUse) && !hasRecords;
}
