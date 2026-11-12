using Microsoft.Extensions.Options;
using OMTS.Application.Common;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Farming.Rafts;
using OMTS.Application.Features.Files;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Application.Features.Cultivation.Cycles;

/// <summary>
/// CYCLE-01 start, edit and cancel, for a Zone Manager of the raft's zone and the Cooperative Owner. These writes notify nobody.
/// A raft holds at most one Open cycle and its cycles never overlap (BR-CYCLE-01); the filtered unique index holds that against
/// parallel starts.
/// </summary>
public sealed class CultivationCycleService(
    ICultivationCycleRepository cycles,
    ICultivationCycleQuery cycleQuery,
    IRaftRepository rafts,
    RaftViews raftViews,
    CultivationCycleViews views,
    CycleWriteGuard writes,
    ZoneAccessGuard zoneAccess,
    RowVersionGuard rowVersions,
    RecordPhotoStore photoStore,
    UploadedFileSaver fileSaver,
    IOptions<CycleStartOptions> startOptions,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    /// <summary>The <c>reasonCode</c> of the refusal to start a cycle by hand (owner decision of 12/11/2026, Q21).</summary>
    public const string RequiresNurseryCode = "CYCLE_REQUIRES_NURSERY";

    /// <summary>The text shown for <see cref="RequiresNurseryCode"/>.</summary>
    public const string RequiresNurseryMessage = "Chu kỳ mới được tạo khi hoàn tất ươm.";

    private const string RecordType = nameof(CultivationCycle);
    private const string Saved = "Cultivation cycle";

    /// <param name="photos">The photos of the plan (owner decision of 12/11/2026): 1 to 3, else 400 on <c>Photos</c> and nothing is saved.</param>
    /// <param name="bootstrap">
    /// True only for the demo bootstrap, which writes demo cycles directly like the rest of its data: no photos, and the nursery rule does not apply. No request can send it.
    /// </param>
    public async Task<Result<CycleStarted>> StartAsync(
        Guid raftId, StartCycleRequest request, IReadOnlyList<IncomingFile> photos, CancellationToken cancellationToken, bool bootstrap = false)
    {
        var photosRequired = !bootstrap;
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

        // A new cycle is created by completing a nursery batch (owner decision of 12/11/2026, Q21): the old way (a start by hand, then a seed lot picked per session with the 110 % and 150 % plan tiers
        // and a stock issue) is closed for new cycles. Cycles already running keep working; the rule is checked after access and read-only, before anything is stored.
        if (!bootstrap && !startOptions.Value.AllowManualStart)
        {
            return new AppError(
                ErrorKind.Validation,
                UserMessages.NurseryRefused(RequiresNurseryMessage),
                new Dictionary<string, object?> { ["reasonCode"] = RequiresNurseryCode, ["field"] = "Cycle", ["hint"] = RequiresNurseryMessage });
        }

        if (await cycleQuery.OpenCycleAsync(raft.Id, cancellationToken) is { } open)
        {
            return RaftStatusService.OpenCycle(open);
        }

        var today = timeProvider.Today();
        var previousClose = await cycles.LastCloseDateAsync(raft.Id, cancellationToken);
        var check = CultivationCycleValues.Read(
            request.StartDate ?? today, request.ExpectedSeedQuantity, request.TargetYieldKg,
            new CycleDateLimits(today, previousClose, null), out var values);
        if (!check.IsValid)
        {
            return AppError.InvalidFields(check.Invalid);
        }

        var files = StepPhotoService.Read(photos, otherFilePart: request.OtherFilePart, minimum: photosRequired ? StepPhotoService.MinPhotos : 0);
        if (!files.IsSuccess)
        {
            return files.Error!;
        }

        var uploaded = await photoStore.UploadAllAsync(FileCategories.CyclePlanPhotos, files.Value!, cancellationToken);
        if (!uploaded.IsSuccess)
        {
            return uploaded.Error!;
        }

        var cycle = new CultivationCycle { RaftId = raft.Id, Raft = raft };
        values.ApplyTo(cycle);
        cycles.Add(cycle);
        var slot = 0;
        foreach (var key in uploaded.Value!)
        {
            var photo = new CyclePlanPhoto { CycleId = cycle.Id, SortOrder = ++slot, FileKey = key };
            cycle.PlanPhotos.Add(photo);
            cycles.AddPlanPhoto(photo);
        }

        // The raft row is part of this save: a parallel lane change or discontinue read "no open cycle", so one of the two fails with MSG06.
        unitOfWork.MarkChanged(raft);
        audit.Record(Entry("CultivationCycle.Started", cycle) with { After = CultivationCycleValues.Snapshot(cycle) });
        AppError? lost;
        try
        {
            lost = await SaveNewAsync(raft, cancellationToken);
        }
        catch
        {
            await fileSaver.DiscardAsync(uploaded.Value!);
            throw;
        }

        if (lost is not null)
        {
            await fileSaver.DiscardAsync(uploaded.Value!);
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
        var check = CultivationCycleValues.Read(request.StartDate, request.ExpectedSeedQuantity, request.TargetYieldKg, limits, out var values);
        if (!check.IsValid)
        {
            return AppError.InvalidFields(check.Invalid);
        }

        if (await rowVersions.CheckAsync(cycle, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        var before = CultivationCycleValues.Snapshot(cycle);
        values.ApplyTo(cycle);
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

        // The plan photos go with the cycle: the rows now, the files once the cancel is saved.
        var planPhotos = await cycles.PlanPhotosForEditAsync(cycle.Id, cancellationToken);
        foreach (var photo in planPhotos)
        {
            cycles.RemovePlanPhoto(photo);
        }

        cycles.Remove(cycle);
        audit.Record(Entry("CultivationCycle.Cancelled", cycle) with { Before = CultivationCycleValues.Snapshot(cycle) });
        await fileSaver.SaveAsync([], planPhotos.Select(p => p.FileKey).ToList(), cancellationToken);
        return true;
    }

    /// <summary>
    /// Changes the plan photos of a cycle started without a nursery batch (decision of 12/11/2026): exactly the kept ones and the new ones remain, 1 to 3 (a cycle saved before the photos may stay with none until
    /// it is given some, but never ends above three). A cycle from a nursery batch shows the completion photos of its batch, so they change there: 409 <c>PLAN_PHOTOS_FROM_NURSERY</c>.
    /// </summary>
    public async Task<Result<CycleSaved>> ChangePlanPhotosAsync(Guid cycleId, ChangePlanPhotosRequest request, IReadOnlyList<IncomingFile> newPhotos, CancellationToken cancellationToken)
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

        if (cycle.NurseryBatchId is not null)
        {
            return AppError.Conflict(
                UserMessages.WrongStatus($"The cultivation cycle of raft {cycle.Raft.Code}", "created from a nursery batch: its plan photos are the completion photos of the batch"),
                new Dictionary<string, object?> { ["reasonCode"] = "PLAN_PHOTOS_FROM_NURSERY", ["nurseryBatchId"] = cycle.NurseryBatchId });
        }

        var current = await cycles.PlanPhotosForEditAsync(cycle.Id, cancellationToken);
        var keep = (request.KeepPhotoIds ?? []).ToHashSet();
        var kept = current.Where(p => keep.Contains(p.Id)).ToList();
        var files = StepPhotoService.Read(newPhotos, kept.Count, request.OtherFilePart, kept.Count == keep.Count, StepPhotoService.MinPhotos);
        if (!files.IsSuccess)
        {
            return files.Error!;
        }

        if (await rowVersions.CheckAsync(cycle, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        var uploaded = await photoStore.UploadAllAsync(FileCategories.CyclePlanPhotos, files.Value!, cancellationToken);
        if (!uploaded.IsSuccess)
        {
            return uploaded.Error!;
        }

        var removed = current.Where(p => !kept.Contains(p)).ToList();
        foreach (var photo in removed)
        {
            cycles.RemovePlanPhoto(photo);
        }

        using (var free = Enumerable.Range(1, CultivationCycle.MaxPlanPhotos).Where(slot => kept.All(p => p.SortOrder != slot)).GetEnumerator())
        {
            foreach (var key in uploaded.Value!)
            {
                free.MoveNext();
                var photo = new CyclePlanPhoto { CycleId = cycle.Id, SortOrder = free.Current, FileKey = key };
                cycles.AddPlanPhoto(photo);
            }
        }

        // The photos are rows of their own: the cycle row is part of the save, so a parallel edit loses with MSG06.
        unitOfWork.MarkChanged(cycle);
        audit.Record(Entry("CultivationCycle.Updated", cycle) with { Before = new { Photos = current.Count }, After = new { Photos = kept.Count + uploaded.Value!.Count } });
        await fileSaver.SaveAsync(uploaded.Value!, removed.Select(p => p.FileKey).ToList(), cancellationToken);
        var saved = UserMessages.Saved(Saved);
        return new CycleSaved(await views.ViewAsync(cycle, cancellationToken), saved.Text, saved.Code);
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

    /// <summary>MSG-GEN-06, as a cycle with records blocks the cancel the way an Open cycle blocks a start or a lane change.</summary>
    private static AppError HasRecords(CultivationCycle cycle, int records) => AppError.Conflict(
        UserMessages.StillInUse(records, $"records of the cultivation cycle started {cycle.StartDate:dd/MM/yyyy}"),
        new Dictionary<string, object?> { ["cycleId"] = cycle.Id });

    private static AuditEntry Entry(string action, CultivationCycle cycle) =>
        new(action, RecordType) { RecordId = cycle.Id, RecordCode = cycle.Raft.Code, ZoneId = cycle.Raft.ZoneId };
}
