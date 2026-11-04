using OMTS.Application.Common;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Cultivation.Cycles;
using OMTS.Application.Features.Farming;
using OMTS.Application.Features.Files;
using OMTS.Domain.Entities.Cultivation;

namespace OMTS.Application.Features.Cultivation.CultivationLogs;

/// <summary>
/// CYCLE-04.1 correct or remove a cultivation log while its cycle is Open, with a reason: a Zone Manager only what they recorded, the Cooperative
/// Owner any log (BR-CYCLE-05). The Owner is not notified again. After the close, a log is corrected only in TRACE-03 (BR-CYCLE-04). Each change
/// moves the row version of the log and of the cycle, so a parallel change, close or new start date fails with MSG06.
/// </summary>
public sealed class CultivationLogCorrectionService(
    ICultivationLogRepository logs,
    CycleWriteGuard writes,
    CultivationLogPhotoStore photoStore,
    UploadedFileSaver fileSaver,
    CultivationLogViews views,
    RowVersionGuard rowVersions,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    public async Task<Result<CultivationLogSaved>> CorrectAsync(
        Guid logId, CorrectCultivationLogRequest request, IReadOnlyList<IncomingFile> newPhotos, CancellationToken cancellationToken)
    {
        var log = await logs.GetForEditAsync(logId, cancellationToken);
        if (log is null)
        {
            return AppError.NotFound;
        }

        if (await RefusalAsync(log, cancellationToken) is { } refused)
        {
            return refused;
        }

        // Ordinal, as the key was stored: another spelling is not this file.
        var keep = (request.KeepPhotoKeys ?? []).Where(k => !string.IsNullOrEmpty(k)).ToHashSet(StringComparer.Ordinal);
        var kept = log.Photos.Where(p => keep.Contains(p.FileKey)).ToList();
        if (CultivationLogValues.Read(
                request.VisitDate, request.Notes, newPhotos, kept.Count, keysAreKnown: kept.Count == keep.Count, request.Reason, reasonRequired: true,
                log.Cycle.StartDate, timeProvider.Today(), out var values) is { } invalid)
        {
            return invalid;
        }

        if (await rowVersions.CheckAsync(log, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        var uploaded = await photoStore.UploadAllAsync(values.NewPhotos, cancellationToken);
        if (!uploaded.IsSuccess)
        {
            return uploaded.Error!;
        }

        var before = CultivationLogValues.Snapshot(log);
        (log.VisitDate, log.Notes) = (values.VisitDate, values.Notes);
        var removedKeys = ApplyPhotos(log, kept, uploaded.Value!);
        var (changedBefore, changedAfter) = AuditDiff.Changed(before, CultivationLogValues.Snapshot(log));
        if (changedAfter is not null)
        {
            // Photos are rows of their own: a change of photos only must still give the log a new row version.
            unitOfWork.MarkChanged(log);
            unitOfWork.MarkChanged(log.Cycle);
            audit.Record(CultivationLogAudit.Entry(CultivationLogAudit.Corrected, log) with { Before = changedBefore, After = changedAfter, Reason = request.Reason!.Trim() });
            await SaveAsync(uploaded.Value!, removedKeys, cancellationToken);
        }

        var saved = UserMessages.Saved("Cultivation log");
        return new CultivationLogSaved(await views.ViewAsync(log, cancellationToken), saved.Text, saved.Code);
    }

    /// <summary>Removes the log and its photos; the audit log keeps them with the reason. With no record left, the cycle can be cancelled again.</summary>
    public async Task<Result<bool>> RemoveAsync(Guid logId, RemoveCultivationLogRequest request, CancellationToken cancellationToken)
    {
        var log = await logs.GetForEditAsync(logId, cancellationToken);
        if (log is null)
        {
            return AppError.NotFound;
        }

        if (await RefusalAsync(log, cancellationToken) is { } refused)
        {
            return refused;
        }

        var check = new FieldCheck();
        CultivationLogValues.CheckReason(check, request.Reason);
        if (!check.IsValid)
        {
            return AppError.InvalidFields(check.Invalid);
        }

        if (await rowVersions.CheckAsync(log, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        var keys = log.Photos.Select(p => p.FileKey).ToList();
        audit.Record(CultivationLogAudit.Entry(CultivationLogAudit.Removed, log) with { Before = CultivationLogValues.Snapshot(log), Reason = request.Reason!.Trim() });
        unitOfWork.MarkChanged(log.Cycle);
        logs.Remove(log);
        await fileSaver.SaveAsync([], keys, cancellationToken);
        return true;
    }

    /// <summary>MSG-GEN-08 when the caller may not correct this log, MSG-GEN-09 when its cycle no longer allows a change.</summary>
    private async Task<AppError?> RefusalAsync(CultivationLog log, CancellationToken cancellationToken) =>
        CultivationLogViews.MayCorrect(await views.AccessAsync(cancellationToken), log, log.Cycle.Raft.ZoneId) ? writes.Refusal(log.Cycle) : AppError.Forbidden;

    /// <summary>Removes the photos not kept and adds the new ones in the free slots; kept photos stay in their slots. Returns the keys of the removed ones.</summary>
    private List<string> ApplyPhotos(CultivationLog log, List<CultivationLogPhoto> kept, IReadOnlyList<string> newKeys)
    {
        var removed = log.Photos.Except(kept).ToList();
        foreach (var photo in removed)
        {
            log.Photos.Remove(photo);
            logs.RemovePhoto(photo);
        }

        var freeSlots = Enumerable.Range(1, CultivationLog.MaxPhotos).Where(slot => kept.All(p => p.SortOrder != slot)).GetEnumerator();
        foreach (var key in newKeys)
        {
            freeSlots.MoveNext();
            var photo = new CultivationLogPhoto { LogId = log.Id, SortOrder = freeSlots.Current, FileKey = key };
            log.Photos.Add(photo);
            logs.AddPhoto(photo);
        }

        return removed.Select(p => p.FileKey).ToList();
    }

    private async Task SaveAsync(IReadOnlyList<string> uploadedKeys, List<string> removedKeys, CancellationToken cancellationToken)
    {
        try
        {
            await fileSaver.SaveAsync(uploadedKeys, removedKeys, cancellationToken);
        }
        catch (DuplicateRecordException exception)
        {
            // A parallel correction took the slot: the same answer as when it changed the log first (MSG06).
            throw new ConcurrencyConflictException(exception);
        }
    }
}
