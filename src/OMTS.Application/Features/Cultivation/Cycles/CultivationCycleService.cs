using OMTS.Application.Common;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Farming.Rafts;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Application.Features.Cultivation.Cycles;

/// <summary>
/// CYCLE-01 start, edit and cancel, for a Zone Manager of the raft's zone and the Cooperative Owner. These writes notify nobody.
/// A raft holds at most one Open cycle and its cycles never overlap (BR-CYCLE-01); the filtered unique index holds that against
/// parallel starts. The product farmed on the raft is chosen when the cycle starts (a product In Use) and can change until the cycle has a harvest lot.
/// </summary>
public sealed class CultivationCycleService(
    ICultivationCycleRepository cycles,
    ICultivationCycleQuery cycleQuery,
    IProductRepository products,
    IStatusCatalog statusCatalog,
    IRaftRepository rafts,
    RaftViews raftViews,
    CultivationCycleViews views,
    CycleWriteGuard writes,
    ZoneAccessGuard zoneAccess,
    RowVersionGuard rowVersions,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    private const string RecordType = nameof(CultivationCycle);
    private const string Saved = "Cultivation cycle";
    private const string ProductField = "Product";

    public async Task<Result<CycleStarted>> StartAsync(Guid raftId, StartCycleRequest request, CancellationToken cancellationToken)
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

        if (raftViews.EditRefusal(raft) is { } refused)
        {
            return refused;
        }

        if (await cycleQuery.OpenCycleAsync(raft.Id, cancellationToken) is { } open)
        {
            return RaftStatusService.OpenCycle(open);
        }

        var today = timeProvider.Today();
        var previousClose = await cycles.LastCloseDateAsync(raft.Id, cancellationToken);
        var check = CultivationCycleValues.Read(
            request.StartDate ?? today, request.ExpectedSeedQuantity, request.ExpectedHarvestDate, request.TargetYieldKg,
            new CycleDateLimits(today, previousClose, null), out var values);
        var product = await FindProductAsync(request.ProductId, cancellationToken);
        check.Check(product is not null, ProductField);
        if (!check.IsValid)
        {
            return AppError.InvalidFields(check.Invalid);
        }

        if (ProductRefusal(product!) is { } notInUse)
        {
            return notInUse;
        }

        var cycle = new CultivationCycle { RaftId = raft.Id, Raft = raft, ProductId = product!.Id, Product = product };
        values.ApplyTo(cycle);
        cycles.Add(cycle);

        // The raft row is part of this save: a parallel lane change or discontinue read "no open cycle", so one of the two fails with MSG06.
        unitOfWork.MarkChanged(raft);
        audit.Record(Entry("CultivationCycle.Started", cycle) with { After = CultivationCycleValues.Snapshot(cycle) });
        if (await SaveNewAsync(raft, cancellationToken) is { } lost)
        {
            return lost;
        }

        var saved = UserMessages.Saved(Saved);
        return new CycleStarted(await views.ViewAsync(cycle, cancellationToken), previousClose, raftViews.Lanes(raft), saved.Text, saved.Code);
    }

    /// <summary>CYCLE-01.1: only an Open cycle, on a raft In Use; the start date stays within the limits of BR-CYCLE-01.</summary>
    public async Task<Result<CycleSaved>> EditAsync(Guid cycleId, EditCycleRequest request, CancellationToken cancellationToken)
    {
        var cycle = await cycles.GetForEditAsync(cycleId, cancellationToken);
        if (cycle is null)
        {
            return AppError.NotFound;
        }

        if (!await zoneAccess.AllowsAsync(PermissionFunctions.CultivationCycles, PermissionAction.Edit, cycle.Raft.ZoneId, cancellationToken))
        {
            return AppError.Forbidden;
        }

        if (writes.Refusal(cycle) is { } refused)
        {
            return refused;
        }

        var limits = new CycleDateLimits(
            timeProvider.Today(),
            await cycles.LastCloseDateAsync(cycle.RaftId, cancellationToken),
            await cycles.FirstRecordDateAsync(cycle.Id, cancellationToken));
        var check = CultivationCycleValues.Read(request.StartDate, request.ExpectedSeedQuantity, request.ExpectedHarvestDate, request.TargetYieldKg, limits, out var values);
        var changesProduct = request.ProductId is { } chosen && chosen != cycle.ProductId;
        var product = changesProduct ? await FindProductAsync(request.ProductId, cancellationToken) : null;
        check.Check(!changesProduct || product is not null, ProductField);
        if (!check.IsValid)
        {
            return AppError.InvalidFields(check.Invalid);
        }

        if (await rowVersions.CheckAsync(cycle, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        if (product is not null)
        {
            if (ProductRefusal(product) is { } notInUse)
            {
                return notInUse;
            }

            if (await cycles.HasHarvestLotAsync(cycle.Id, cancellationToken))
            {
                return ProductLocked(cycle);
            }
        }

        var before = CultivationCycleValues.Snapshot(cycle);
        values.ApplyTo(cycle);
        if (product is not null)
        {
            cycle.ProductId = product.Id;
            cycle.Product = product;
        }

        var (changedBefore, changedAfter) = AuditDiff.Changed(before, CultivationCycleValues.Snapshot(cycle));
        if (changedAfter is not null)
        {
            audit.Record(Entry("CultivationCycle.Updated", cycle) with { Before = changedBefore, After = changedAfter });
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        var saved = UserMessages.Saved(Saved);
        return new CycleSaved(await views.ViewAsync(cycle, cancellationToken), saved.Text, saved.Code);
    }

    /// <summary>CYCLE-01.2: an Open cycle with no record is removed, and kept only in the audit log.</summary>
    public async Task<Result<bool>> CancelAsync(Guid cycleId, CancelCycleRequest request, CancellationToken cancellationToken)
    {
        var cycle = await cycles.GetForEditAsync(cycleId, cancellationToken);
        if (cycle is null)
        {
            return AppError.NotFound;
        }

        if (!await zoneAccess.AllowsAsync(PermissionFunctions.CultivationCycles, PermissionAction.Delete, cycle.Raft.ZoneId, cancellationToken))
        {
            return AppError.Forbidden;
        }

        if (writes.Refusal(cycle) is { } refused)
        {
            return refused;
        }

        var records = await cycles.CountRecordsAsync(cycle.Id, cancellationToken);
        if (records > 0)
        {
            return HasRecords(cycle, records);
        }

        if (await rowVersions.CheckAsync(cycle, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        cycles.Remove(cycle);
        audit.Record(Entry("CultivationCycle.Cancelled", cycle) with { Before = CultivationCycleValues.Snapshot(cycle) });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Saves a new cycle with its raft row. A parallel start usually loses on the row version of the raft, sometimes on the filtered
    /// index or as the deadlock victim: each answers what the pre-check answers. With no Open cycle, the raft itself changed meanwhile (MSG06).
    /// </summary>
    private async Task<AppError?> SaveNewAsync(Raft raft, CancellationToken cancellationToken)
    {
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return null;
        }
        catch (Exception exception) when (exception is ConcurrencyConflictException or DuplicateRecordException)
        {
            if (await cycleQuery.OpenCycleAsync(raft.Id, cancellationToken) is { } open)
            {
                return RaftStatusService.OpenCycle(open);
            }

            if (exception is DuplicateRecordException)
            {
                throw new ConcurrencyConflictException(exception);
            }

            throw;
        }
    }

    /// <summary>The product chosen, null when it was not sent, does not exist or is deleted (hidden): MSG-GEN-01 names the field.</summary>
    private async Task<Product?> FindProductAsync(Guid? productId, CancellationToken cancellationToken) =>
        productId is { } id ? (await products.GetByIdsAsync([id], cancellationToken)).FirstOrDefault(p => !p.IsHidden) : null;

    /// <summary>MSG-GEN-09 (GBR-01) when the product chosen is Discontinued, which is not offered for a new choice.</summary>
    private AppError? ProductRefusal(Product product) => product.StatusId == StatusIds.Usage.InUse
        ? null
        : AppError.NotUsableChoice($"Product {product.Code}", statusCatalog.DisplayNameOf(product.StatusId), ProductField);

    /// <summary>MSG-GEN-09: the product is fixed once the cycle has a harvest lot, recorded and sold under it. Not MSG-GEN-06, which makes the screen ask for a reload.</summary>
    private static AppError ProductLocked(CultivationCycle cycle) => AppError.Conflict(
        UserMessages.WrongStatus($"The cultivation cycle of raft {cycle.Raft.Code}", "with harvest lots"),
        new Dictionary<string, object?> { ["cycleId"] = cycle.Id, ["field"] = ProductField });

    /// <summary>MSG-GEN-06, as a cycle with records blocks the cancel the way an Open cycle blocks a start or a lane change.</summary>
    private static AppError HasRecords(CultivationCycle cycle, int records) => AppError.Conflict(
        UserMessages.StillInUse(records, $"records of the cultivation cycle started {cycle.StartDate:dd/MM/yyyy}"),
        new Dictionary<string, object?> { ["cycleId"] = cycle.Id });

    private static AuditEntry Entry(string action, CultivationCycle cycle) =>
        new(action, RecordType) { RecordId = cycle.Id, RecordCode = cycle.Raft.Code, ZoneId = cycle.Raft.ZoneId };
}
