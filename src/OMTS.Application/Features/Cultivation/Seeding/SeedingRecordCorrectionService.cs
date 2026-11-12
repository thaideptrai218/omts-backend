using OMTS.Application.Common;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Cultivation.Cycles;
using OMTS.Application.Features.Cultivation.Nursery;
using OMTS.Application.Features.Cultivation.Progress;
using OMTS.Application.Features.Farming;
using OMTS.Application.Features.Files;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Application.Features.Cultivation.Seeding;

/// <summary>
/// CYCLE-02.2 correct or remove a seeding session with a reason and without the Owner's review: a Zone Manager only what they recorded, the Cooperative Owner any session, while the
/// cycle is Open and no lane of the session is in a harvest lot (BR-CYCLE-02). After that it is corrected only in TRACE-03. It notifies nobody. A session carries a version (NFR-09
/// excepts only logs and water measurements). The cycle row is part of the save, as the lane states depend on it, and so is the receipt row of the lot the session ends on.
/// </summary>
public sealed class SeedingRecordCorrectionService(
    ISeedingRecordRepository records,
    INurseryBatchRepository nurseries,
    IRaftRepository rafts,
    CycleWriteGuard writes,
    SeedingSessionReader reader,
    SeedingViews views,
    LaneStateReader laneStates,
    RowVersionGuard rowVersions,
    SeedingStockSync stockSync,
    LanePercentGuard percents,
    RecordPhotoStore photoStore,
    UploadedFileSaver fileSaver,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    /// <param name="newPhotos">New photos (owner decision of 12/11/2026). A session that has photos keeps 1 to 3 after the correction; one saved before the photos stays valid with none.</param>
    public async Task<Result<SeedingRecordSaved>> CorrectAsync(Guid recordId, CorrectSeedingRequest request, IReadOnlyList<IncomingFile> newPhotos, CancellationToken cancellationToken)
    {
        var record = await records.GetForEditAsync(recordId, cancellationToken);
        if (record is null)
        {
            return AppError.NotFound;
        }

        var (raft, states) = await LanesAsync(record, cancellationToken);
        if (await RefusalAsync(record, states, cancellationToken) is { } refused)
        {
            return refused;
        }

        // A record of a cycle that draws from a nursery batch (v2, 13/11/2026) takes any quantity up to what the pool of the batch has left once this record is set aside; it is not corrected after the seeding of the cycle is
        // marked complete. The pool is the sum of the records, so a correction gives back or takes the difference by itself.
        var batch = record.NurseryBatchId is { } batchId ? await nurseries.GetForPoolAsync(batchId, cancellationToken) : null;
        if (batch is not null && record.Cycle.SeedingCompletedOn is not null)
        {
            return NurseryRules.SeedingCompletedRefusal(record.Cycle.Raft.Code);
        }

        var input = new SeedingInput(request.SeedingDate, request.SeedLotId, request.LaneNumbers, request.Quantity);        var (fields, invalid) = await reader.ReadAsync(record.Cycle, input, record, request.Reason, reasonRequired: true, cancellationToken);
        if (invalid is not null)
        {
            return invalid;
        }

        if (await rowVersions.CheckAsync(record, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        // The photos after the correction: all of them unless the form names the ones to keep; a session that has photos keeps at least one, one saved before the photos may stay without.
        var keep = (request.KeepPhotoIds ?? []).ToHashSet();
        var kept = request.ChangesPhotos ? record.Photos.Where(p => keep.Contains(p.Id)).ToList() : record.Photos.ToList();
        var files = request.ChangesPhotos || newPhotos.Count > 0
            ? StepPhotoService.Read(newPhotos, kept.Count, request.OtherFilePart, kept.Count == keep.Count || !request.ChangesPhotos, record.Photos.Count > 0 ? StepPhotoService.MinPhotos : 0)
            : Result<IReadOnlyList<ValidatedFile>>.Success([]);
        if (!files.IsSuccess)
        {
            return files.Error!;
        }

        var shares = percents.ForSeeding(
            await percents.ReadAsync(record.CycleId, cancellationToken), fields!.Lanes, request.LanePercents, request.LanePercentsUnreadable,
            record.Lanes.ToDictionary(l => l.LaneId, l => l.SeededPercent), record.Id);
        if (!shares.IsSuccess)
        {
            return shares.Error!;
        }

        // A record from a nursery is capped by the pool of its batch (the other records of every cycle taken off); the others keep the plan rules, judged only when the correction raises the seed, as for a harvest lot.
        string? overPlanReason = null;
        if (batch is not null)
        {
            var seededByOthers = (await nurseries.SeededByBatchAsync([batch.Id], record.Id, cancellationToken)).GetValueOrDefault(batch.Id);
            if (NurseryRules.CheckPool(batch, seededByOthers, fields!.Quantity) is { } overPool)
            {
                return overPool;
            }
        }
        else if (fields!.Quantity > record.Quantity)
        {
            var others = (await records.ListByCycleAsync(record.CycleId, cancellationToken)).Where(r => r.Id != record.Id).Sum(r => r.Quantity);
            if (SeedingPlanRules.Check(new SeedingPlanInput(record.Cycle.ExpectedSeedQuantity, others, request.ConfirmOverPlan, request.OverPlanReason), fields.Quantity, out overPlanReason) is { } overPlan)
            {
                return overPlan;
            }
        }
        var uploaded = await photoStore.UploadAllAsync(FileCategories.SeedingPhotos, files.Value!, cancellationToken);
        if (!uploaded.IsSuccess)
        {
            return uploaded.Error!;
        }

        var before = SeedingValues.Snapshot(record);
        // A refusal or a failure before the save removes the uploads: no row will point to them.
        var replaced = await photoStore.PrepareSaveAsync(uploaded.Value!, async () =>
        {
            if (await stockSync.ReplaceAsync(record, fields!, raft, cancellationToken) is { } stockRefused)
            {
                return Result<bool>.Failure(stockRefused);
            }

            return Result<bool>.Success(true);
        });
        if (!replaced.IsSuccess)
        {
            return replaced.Error!;
        }

        SeedingRecordRows.Apply(records, record, fields!, shares.Value);
        record.OverPlanReason = overPlanReason ?? record.OverPlanReason;
        var photosChanged = request.ChangesPhotos || uploaded.Value!.Count > 0;
        var removedKeys = photosChanged ? SeedingPhotoRows.Replace(records, record, kept, uploaded.Value!) : [];
        var (changedBefore, changedAfter) = AuditDiff.Changed(before, SeedingValues.Snapshot(record));
        if (changedAfter is not null || removedKeys.Count > 0 || uploaded.Value!.Count > 0)
        {
            // Lanes and photos are rows of their own: a change of them only must still give the session a new version.
            unitOfWork.MarkChanged(record);
            unitOfWork.MarkChanged(record.Cycle);
            if (batch is not null)
            {
                unitOfWork.MarkChanged(batch);
            }
            else
            {
                unitOfWork.MarkChanged(fields!.Lot.Receipt);
            }

            audit.Record(SeedingValues.Entry(SeedingValues.Corrected, record) with { Before = changedBefore ?? before, After = changedAfter ?? SeedingValues.Snapshot(record), Reason = fields!.Reason });
            await fileSaver.SaveAsync(uploaded.Value!, removedKeys, cancellationToken);
        }

        return await views.SavedAsync(record, raft, laneStates, timeProvider.Today(), cancellationToken);
    }

    /// <summary>Removes the session; the audit log keeps it with the reason. Its quantity leaves the seed lot, and with no record left the cycle can be cancelled again.</summary>
    public async Task<Result<bool>> RemoveAsync(Guid recordId, RemoveSeedingRequest request, CancellationToken cancellationToken)
    {
        var record = await records.GetForEditAsync(recordId, cancellationToken);
        if (record is null)
        {
            return AppError.NotFound;
        }

        var (_, states) = await LanesAsync(record, cancellationToken);
        if (await RefusalAsync(record, states, cancellationToken) is { } refused)
        {
            return refused;
        }

        if (record.NurseryBatchId is not null && record.Cycle.SeedingCompletedOn is not null)
        {
            return NurseryRules.SeedingCompletedRefusal(record.Cycle.Raft.Code);
        }

        var check = new FieldCheck();
        var reason = check.Required(request.Reason, SeedingValues.ReasonField, SeedingValues.MaxReasonLength);
        if (!check.IsValid)
        {
            return AppError.InvalidFields(check.Invalid);
        }

        if (await rowVersions.CheckAsync(record, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        audit.Record(SeedingValues.Entry(SeedingValues.Removed, record) with { Before = SeedingValues.Snapshot(record), Reason = reason });
        unitOfWork.MarkChanged(record.Cycle);
        await stockSync.RemoveAsync(record, record.Cycle.Raft, reason, cancellationToken);
        records.Remove(record);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task<(Raft Raft, IReadOnlyDictionary<Guid, LaneState> States)> LanesAsync(SeedingRecord record, CancellationToken cancellationToken)
    {
        var raft = await rafts.GetForEditAsync(record.Cycle.RaftId, cancellationToken) ?? record.Cycle.Raft;
        return (raft, (await laneStates.ReadAsync(raft, timeProvider.Today(), cancellationToken)).States);
    }

    /// <summary>MSG-GEN-08 when the caller may not correct this session, MSG-GEN-09 when its cycle or a harvest lot no longer allows it.</summary>
    private async Task<AppError?> RefusalAsync(SeedingRecord record, IReadOnlyDictionary<Guid, LaneState> states, CancellationToken cancellationToken)
    {
        if (!SeedingViews.MayCorrect(await views.AccessAsync(cancellationToken), record, record.Cycle.Raft.ZoneId))
        {
            return AppError.Forbidden;
        }

        if (writes.Refusal(record.Cycle) is { } refused)
        {
            return refused;
        }

        return record.Lanes.Any(l => states.TryGetValue(l.LaneId, out var state) && SeedingLaneRules.IsInAHarvestLot(state))
            ? AppError.Conflict(UserMessages.WrongStatus($"The seeding session of raft {record.Cycle.Raft.Code} on {record.SeedingDate:dd/MM/yyyy}", "in a harvest lot"))
            : null;
    }
}
