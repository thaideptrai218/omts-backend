using OMTS.Application.Common;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Cultivation.Nursery;
using OMTS.Application.Features.Farming;
using OMTS.Application.Features.Farming.Rafts;
using OMTS.Application.Features.Files;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Application.Features.Cultivation.Cycles;

/// <summary>
/// CYCLE-01 (v2, 13/11/2026): a cycle is started by hand and always from a nursery batch that is ready ("đã ươm xong", same zone as the raft, pieces left in its pool), with the expected total yield and 1 to 3
/// plan photos. The batch of a cycle can be changed only before its first seeding record; the yield, the start date and the note any time while the cycle is Open. The old way (a free seed lot chosen per session)
/// is closed: <see cref="StartAsync"/> is reached only by the demo bootstrap and the test seam.
/// </summary>
public sealed partial class CultivationCycleService
{
    private const string NurseryBatchField = "Nursery batch";
    private const string TargetYieldField = "Target yield";
    private const string NoteField = "Note";

    public async Task<Result<CycleStarted>> CreateAsync(CreateCycleRequest request, IReadOnlyList<IncomingFile> photos, CancellationToken cancellationToken)
    {
        if (request.RaftId is not { } raftId || raftId == Guid.Empty)
        {
            return AppError.Invalid("Raft", "Chọn bè để bắt đầu chu kỳ nuôi.");
        }

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

        var (batch, batchError) = await CheckBatchAsync(request.NurseryBatchId, raft, cancellationToken);
        if (batchError is not null)
        {
            return batchError;
        }

        var today = timeProvider.Today();
        var previousClose = await cycles.LastCloseDateAsync(raft.Id, cancellationToken);
        var check = CultivationCycleValues.Read(request.StartDate ?? today, null, request.TargetYieldKg, new CycleDateLimits(today, previousClose, null), out var values);
        check.Check(request.TargetYieldKg is not null, TargetYieldField);
        var note = check.Optional(request.Note, NoteField, CultivationCycle.MaxNoteLength);
        if (!check.IsValid)
        {
            return AppError.InvalidFields(check.Invalid.Distinct().ToList());
        }

        var files = StepPhotoService.Read(photos, otherFilePart: request.OtherFilePart, minimum: StepPhotoService.MinPhotos);
        if (!files.IsSuccess)
        {
            return files.Error!;
        }

        var uploaded = await photoStore.UploadAllAsync(FileCategories.CyclePlanPhotos, files.Value!, cancellationToken);
        if (!uploaded.IsSuccess)
        {
            return uploaded.Error!;
        }

        var cycle = new CultivationCycle { RaftId = raft.Id, Raft = raft, NurseryBatchId = batch!.Id, Note = note };
        values.ApplyTo(cycle);
        cycles.Add(cycle);
        var slot = 0;
        foreach (var key in uploaded.Value!)
        {
            var photo = new CyclePlanPhoto { CycleId = cycle.Id, SortOrder = ++slot, FileKey = key };
            cycle.PlanPhotos.Add(photo);
            cycles.AddPlanPhoto(photo);
        }

        // The raft row and the batch row are part of this save: a parallel lane change or discontinue, or a parallel close or correction of the batch, read the state this start read, so one of the two fails with MSG06.
        unitOfWork.MarkChanged(raft);
        unitOfWork.MarkChanged(batch);
        audit.Record(Entry("CultivationCycle.Started", cycle) with { After = CultivationCycleValues.NurserySnapshot(cycle, batch.Code) });
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

    /// <summary>CYCLE-01.1 of a cycle that draws from a nursery batch (or is given one now): the yield, the start date, the note and, before the first seeding record, the batch.</summary>
    private async Task<Result<CycleSaved>> EditNurseryCycleAsync(CultivationCycle cycle, EditCycleRequest request, CancellationToken cancellationToken)
    {
        var limits = new CycleDateLimits(
            timeProvider.Today(),
            await cycles.LastCloseDateAsync(cycle.RaftId, cancellationToken),
            await cycles.FirstRecordDateAsync(cycle.Id, cancellationToken));
        var check = CultivationCycleValues.Read(request.StartDate ?? cycle.StartDate, cycle.ExpectedSeedQuantity, request.TargetYieldKg ?? cycle.TargetYieldKg, limits, out var values);
        check.Check((request.TargetYieldKg ?? cycle.TargetYieldKg) is not null, TargetYieldField);
        var note = request.Note is null ? cycle.Note : check.Optional(request.Note, NoteField, CultivationCycle.MaxNoteLength);
        if (!check.IsValid)
        {
            return AppError.InvalidFields(check.Invalid.Distinct().ToList());
        }

        NurseryBatch? newBatch = null;
        if (request.NurseryBatchId is { } wanted && wanted != cycle.NurseryBatchId)
        {
            if ((await nurseries.SeededByCycleAsync([cycle.Id], cancellationToken)).GetValueOrDefault(cycle.Id) > 0m)
            {
                return NurseryRules.Refused(
                    "Chu kỳ đã có bản ghi thả giống nên không thể đổi đợt ươm. Hãy xóa các bản ghi thả giống trước.", NurseryReasonCodes.CycleSeedLocked,
                    new Dictionary<string, object?> { ["field"] = NurseryBatchField, ["cycleId"] = cycle.Id });
            }

            var (batch, batchError) = await CheckBatchAsync(wanted, cycle.Raft, cancellationToken);
            if (batchError is not null)
            {
                return batchError;
            }

            newBatch = batch;
        }

        if (await rowVersions.CheckAsync(cycle, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        var oldBatch = cycle.NurseryBatchId is { } oldId ? await nurseries.GetForPoolAsync(oldId, cancellationToken) : null;
        var before = CultivationCycleValues.NurserySnapshot(cycle, oldBatch?.Code);
        values.ApplyTo(cycle);
        cycle.Note = note;
        if (newBatch is not null)
        {
            cycle.NurseryBatchId = newBatch.Id;
            unitOfWork.MarkChanged(newBatch);
        }

        var (changedBefore, changedAfter) = AuditDiff.Changed(before, CultivationCycleValues.NurserySnapshot(cycle, newBatch?.Code ?? oldBatch?.Code));
        if (changedAfter is not null)
        {
            audit.Record(Entry("CultivationCycle.Updated", cycle) with { Before = changedBefore, After = changedAfter });
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        var saved = UserMessages.Saved(Saved);
        return new CycleSaved(await views.ViewAsync(cycle, cancellationToken), saved.Text, saved.Code);
    }

    /// <summary>
    /// The batch a cycle may draw from (v2): it exists, is in the zone of the raft, is Completed (ready) and has pieces left in its pool. Null batch and an error when it does not qualify; the batch is tracked and comes with its seed lot.
    /// </summary>
    private async Task<(NurseryBatch? Batch, AppError? Error)> CheckBatchAsync(Guid? batchId, Raft raft, CancellationToken cancellationToken)
    {
        if (batchId is not { } id || id == Guid.Empty)
        {
            return (null, AppError.Invalid(NurseryBatchField, "Chọn một đợt ươm đã ươm xong để nuôi."));
        }

        var batch = await nurseries.GetForPoolAsync(id, cancellationToken);
        if (batch is null)
        {
            return (null, AppError.NotFound);
        }

        var details = new Dictionary<string, object?> { ["field"] = NurseryBatchField, ["nurseryBatchId"] = batch.Id, ["nurseryCode"] = batch.Code };
        if (batch.ZoneId != raft.ZoneId)
        {
            return (null, NurseryRules.Refused($"Đợt ươm {batch.Code} thuộc vùng {batch.Zone.Code}, khác vùng của bè {raft.Code}.", NurseryReasonCodes.RaftOtherZone, details));
        }

        if (batch.StatusId != StatusIds.NurseryBatch.Completed)
        {
            return (null, NurseryRules.NotCompleted(batch.Code, NurseryValues.StatusLabel(batch.StatusId)));
        }

        var seeded = (await nurseries.SeededByBatchAsync([batch.Id], null, cancellationToken)).GetValueOrDefault(batch.Id);
        var remaining = NurseryRules.Remaining(batch.Survivors ?? 0m, seeded);
        if (remaining <= 0m)
        {
            details["remaining"] = remaining;
            return (null, NurseryRules.Refused($"Kho ươm {batch.Code} đã hết mảnh, không thể bắt đầu chu kỳ từ đợt này.", NurseryReasonCodes.PoolEmpty, details));
        }

        return (batch, null);
    }
}
