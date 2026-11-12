using OMTS.Application.Common;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Cultivation.Seeding;
using OMTS.Application.Features.Files;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Purchasing;

namespace OMTS.Application.Features.Cultivation.Nursery;

/// <summary>The nursery answers, with the figures and commands worked out when shown. Every caller has already passed the zone check on the batch.</summary>
public sealed class NurseryBatchViews(
    INurseryBatchRepository nurseries,
    ISeedLotRepository lots,
    ISeedLotUsageQuery usage,
    IAccountRepository accounts,
    StepPhotoService photos,
    ZoneAccessGuard zoneAccess,
    IStatusCatalog statusCatalog,
    TimeProvider timeProvider)
{
    public async Task<NurseryBatchView> ViewAsync(NurseryBatch batch, CancellationToken cancellationToken)
    {
        var lot = batch.SeedLotLine;
        var zoneInUse = batch.Zone.StatusId == StatusIds.Usage.InUse;
        var nursing = batch.StatusId == StatusIds.NurseryBatch.Nursing;
        var completed = batch.StatusId == StatusIds.NurseryBatch.Completed;
        var mayEdit = zoneInUse && await zoneAccess.AllowsAsync(PermissionFunctions.CultivationCycles, PermissionAction.Edit, batch.ZoneId, cancellationToken);
        var mayCreate = zoneInUse && await zoneAccess.AllowsAsync(PermissionFunctions.CultivationCycles, PermissionAction.Create, batch.ZoneId, cancellationToken);
        var seeded = await nurseries.SeededByCycleAsync(batch.Allocations.Select(a => a.CycleId).ToList(), cancellationToken);
        var recordedBy = batch.CreatedBy is { } creator ? (await accounts.GetDisplayNamesAsync([creator], cancellationToken)).GetValueOrDefault(creator) : null;
        var loss = LossOf(batch);
        return new NurseryBatchView(
            batch.Id, batch.Code, batch.ZoneId, batch.Zone.Code, batch.Zone.Name, SeedingViews.LotRef(lot), await OnHandAsync(lot, cancellationToken), batch.QuantityIn, UnitOf(lot), batch.StartDate, batch.Note,
            DaysNursing(batch), batch.StatusId, statusCatalog.DisplayNameOf(batch.StatusId), NurseryValues.StatusLabel(batch.StatusId), batch.CompletedOn, batch.Survivors, loss?.Pieces, loss?.Percent, loss is not null,
            batch.IssueId, batch.Issue?.Code, batch.CancelReason, batch.CancelledAt, batch.CreatedBy, recordedBy, batch.CreatedAt,
            await PhotosAsync(batch, NurseryPhotoPhase.Start, cancellationToken), await PhotosAsync(batch, NurseryPhotoPhase.Completion, cancellationToken),
            batch.Allocations.OrderBy(a => a.Raft.Code, StringComparer.Ordinal).Select(a => Allocation(a, seeded.GetValueOrDefault(a.CycleId))).ToList(),
            nursing && mayEdit, nursing && mayCreate, completed && mayEdit, RowVersionGuard.Encode(batch.RowVersion));
    }

    /// <summary>A row of the list: the batch comes with its zone, seed lot (item, receipt, supplier) and allocations.</summary>
    public NurseryBatchListItem ListItem(NurseryBatch batch)
    {
        var lot = batch.SeedLotLine;
        var loss = LossOf(batch);
        return new NurseryBatchListItem(
            batch.Id, batch.Code, batch.ZoneId, batch.Zone.Code, batch.Zone.Name, SeedingValues.LotCode(lot), lot.Item?.Name, UnitOf(lot), lot.Receipt.Supplier.Name, batch.QuantityIn, batch.StartDate,
            DaysNursing(batch), batch.StatusId, statusCatalog.DisplayNameOf(batch.StatusId), NurseryValues.StatusLabel(batch.StatusId), batch.CompletedOn, batch.Survivors, loss?.Pieces, loss?.Percent, loss is not null,
            batch.Allocations.Count, RowVersionGuard.Encode(batch.RowVersion));
    }

    public static NurseryListSummary Summary(NurseryListTotals totals) => new(
        totals.QuantityIn, totals.NursingQuantityIn, totals.CompletedQuantityIn, totals.Survivors,
        NurseryRules.LossPieces(totals.CompletedQuantityIn, totals.Survivors), totals.CompletedQuantityIn > 0 ? NurseryRules.LossPercent(totals.CompletedQuantityIn, totals.Survivors) : null,
        totals.NursingCount, totals.CompletedCount);

    public NurseryAllocationView Allocation(NurseryBatchAllocation a, decimal seeded) => new(
        a.Id, a.RaftId, a.Raft.Code, a.CycleId, a.Cycle.StartDate, a.Cycle.StatusId == StatusIds.CultivationCycle.Open ? "Open" : "Closed", a.Quantity, seeded, NurseryRules.Remaining(a.Quantity, seeded),
        a.Cycle.TargetYieldKg, a.Cycle.StatusId == StatusIds.CultivationCycle.Open);

    public static string UnitOf(PurchaseReceiptLine lot) => lot.Item?.Unit ?? lot.Unit;

    /// <summary>On hand in the lot now: received, less returns and everything consumed (the issue of every nursery counts).</summary>
    public async Task<decimal> OnHandAsync(PurchaseReceiptLine lot, CancellationToken cancellationToken)
    {
        var consumed = (await usage.SeededAsync([lot.Id], cancellationToken)).GetValueOrDefault(lot.Id);
        return (lot.Quantity ?? 0m) - await lots.ReturnedAsync(lot.Id, cancellationToken) - consumed;
    }

    /// <summary>The loss is known once the survivors are (Completed); an estimate, as the survivors are counted by eye.</summary>
    private static (decimal Pieces, decimal Percent)? LossOf(NurseryBatch batch) =>
        batch.Survivors is { } survivors && batch.StatusId == StatusIds.NurseryBatch.Completed
            ? (NurseryRules.LossPieces(batch.QuantityIn, survivors), NurseryRules.LossPercent(batch.QuantityIn, survivors))
            : null;

    private int DaysNursing(NurseryBatch batch)
    {
        var end = batch.CompletedOn ?? (batch.CancelledAt is { } cancelled ? BusinessClock.ToBusinessDate(cancelled) : timeProvider.Today());
        return Math.Max(1, end.DayNumber - batch.StartDate.DayNumber + 1);
    }

    public async Task<IReadOnlyList<NurseryPhotoView>> PhotosAsync(NurseryBatch batch, NurseryPhotoPhase phase, CancellationToken cancellationToken) =>
        (await photos.ViewAllAsync(batch.Photos.Where(p => p.Phase == phase).OrderBy(p => p.SortOrder).Select(p => (p.Id, p.FileKey)), cancellationToken))
            .Select(p => new NurseryPhotoView(p.Id, p.Url)).ToList();
}
