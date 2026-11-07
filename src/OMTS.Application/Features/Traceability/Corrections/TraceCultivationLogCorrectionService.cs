using OMTS.Application.Common;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Cultivation.CultivationLogs;
using OMTS.Application.Features.Files;
using OMTS.Domain.Entities.Cultivation;

namespace OMTS.Application.Features.Traceability.Corrections;

/// <summary>
/// TRACE-03.1 correct a cultivation log of a Closed cycle: its visit date, notes or photos, with the rules of CYCLE-04 and a reason; never removed (BR-TRACE-04). The visit stays
/// within the cycle, from its start to its close date. A log is no origin information (BR-FARM-09), so no harvest lot is listed and none is checked for seed lots (BR-TRACE-05).
/// A Zone Manager confirms the Owner's agreement (MSG-TRACE-04 without lots); the Owner only confirms. As in CYCLE-04.1 a log carries no version (NFR-09): the row is read again
/// after the uploads, and the log and cycle rows are part of the save, so two saves in the same moment answer MSG06 on one side with nothing saved and no file lost.
/// </summary>
public sealed class TraceCultivationLogCorrectionService(
    ICultivationLogRepository logs,
    TraceCorrectionGuard guard,
    RecordPhotoStore photoStore,
    UploadedFileSaver fileSaver,
    CultivationLogViews views,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    private sealed record Applied(CultivationLog Log, List<string> RemovedKeys, bool Changed);

    /// <summary>Steps 5 and 6 for a log: the log as it is and what to ask. Changes nothing; the fields are checked with the save, as the new photos come only with it.</summary>
    public async Task<Result<TraceCorrectionPreview>> PreviewAsync(Guid logId, CancellationToken cancellationToken)
    {
        var log = await logs.GetAsync(logId, cancellationToken);
        if (log is null)
        {
            return AppError.NotFound;
        }

        if (await RefusalAsync(log, cancellationToken) is { } refused)
        {
            return refused;
        }

        return TraceCorrectionAnswers.Preview(
            CultivationLogAudit.RecordType, log.Id, CultivationLogValues.Snapshot(log), null, null, [], await guard.IsOwnerAsync(cancellationToken), string.Empty);
    }

    /// <param name="ownerApproved">The Zone Manager's answer to MSG-TRACE-04; the Owner never needs it.</param>
    public async Task<Result<CultivationLogSaved>> CorrectAsync(
        Guid logId, CorrectCultivationLogRequest request, bool ownerApproved, IReadOnlyList<IncomingFile> newPhotos, CancellationToken cancellationToken)
    {
        // A first look, not tracked: a request that will be refused stores nothing.
        var seen = await logs.GetAsync(logId, cancellationToken);
        if (seen is null)
        {
            return AppError.NotFound;
        }

        if (await RefusalAsync(seen, cancellationToken) is { } refused)
        {
            return refused;
        }

        // Ordinal, as the key was stored: another spelling is not this file.
        var keep = (request.KeepPhotoKeys ?? []).Where(k => !string.IsNullOrEmpty(k)).ToHashSet(StringComparer.Ordinal);
        var keptCount = seen.Photos.Count(p => keep.Contains(p.FileKey));
        if (CultivationLogValues.Read(
                request.VisitDate, request.Notes, newPhotos, request.OtherFilePart, keptCount, keysAreKnown: keptCount == keep.Count, request.Reason, reasonRequired: true,
                seen.Cycle.StartDate, LastDay(seen.Cycle), out var values) is { } invalid)
        {
            return invalid;
        }

        var isOwner = await guard.IsOwnerAsync(cancellationToken);
        if (!isOwner && !ownerApproved)
        {
            return TraceCorrectionAnswers.AgreementRequired([]);
        }

        var uploaded = await photoStore.UploadAllAsync(FileCategories.CultivationLogPhotos, values.NewPhotos, cancellationToken);
        if (!uploaded.IsSuccess)
        {
            return uploaded.Error!;
        }

        var newKeys = uploaded.Value!;
        var applied = await photoStore.PrepareSaveAsync(newKeys, () => ApplyAsync(logId, keep, values, newKeys, request.Reason!.Trim(), !isOwner, cancellationToken));
        if (!applied.IsSuccess)
        {
            return applied.Error!;
        }

        if (applied.Value!.Changed)
        {
            await SaveAsync(newKeys, applied.Value.RemovedKeys, cancellationToken);
        }

        var saved = UserMessages.Saved("Cultivation log");
        return new CultivationLogSaved(await views.ViewAsync(applied.Value.Log, cancellationToken), saved.Text, saved.Code);
    }

    /// <summary>Changes the row as it is after the uploads; a log whose cycle or raft no longer allows it, a start date moved past the visit, or a photo to keep gone refuses.</summary>
    private async Task<Result<Applied>> ApplyAsync(
        Guid logId, HashSet<string?> keep, CultivationLogValues values, IReadOnlyList<string> newKeys, string reason, bool ownerApprovalConfirmed, CancellationToken cancellationToken)
    {
        var log = await logs.GetForEditAsync(logId, cancellationToken);
        if (log is null)
        {
            return AppError.NotFound;
        }

        var kept = log.Photos.Where(p => keep.Contains(p.FileKey)).ToList();
        if ((await RefusalAsync(log, cancellationToken) ?? CultivationLogValues.ReadAgain(values.VisitDate, log.Cycle.StartDate, kept.Count == keep.Count)) is { } refused)
        {
            return refused;
        }

        var before = CultivationLogValues.Snapshot(log);
        (log.VisitDate, log.Notes) = (values.VisitDate, values.Notes);
        var removedKeys = CultivationLogPhotoRows.Apply(logs, log, kept, newKeys);
        var (changedBefore, changedAfter) = AuditDiff.Changed(before, CultivationLogValues.Snapshot(log));
        if (changedAfter is not null)
        {
            // Photos are rows of their own: a change of photos only must still give the log a new row version.
            unitOfWork.MarkChanged(log);
            unitOfWork.MarkChanged(log.Cycle);
            audit.Record(CultivationLogAudit.Entry(TraceCorrectionAudit.CultivationLogCorrected, log) with
            {
                Before = changedBefore, After = TraceCorrectionAudit.After(changedAfter, [], ownerApprovalConfirmed), Reason = reason,
            });
        }

        return new Applied(log, removedKeys, changedAfter is not null);
    }

    /// <summary>MSG-GEN-08 without PROVENANCE in the zone; MSG-GEN-09 while the cycle is Open (CYCLE-04.1 corrects it then) or when the raft is read-only.</summary>
    private async Task<AppError?> RefusalAsync(CultivationLog log, CancellationToken cancellationToken) =>
        await guard.MayCorrectAsync(log.Cycle.Raft.ZoneId, cancellationToken) ? guard.ClosedCycleRefusal(log.Cycle, log.Cycle.Raft) : AppError.Forbidden;

    /// <summary>A visit belongs to its cycle: not after its close date, nor after today.</summary>
    private DateOnly LastDay(CultivationCycle cycle)
    {
        var today = timeProvider.Today();
        return cycle.CloseDate is { } closed && closed < today ? closed : today;
    }

    private async Task SaveAsync(IReadOnlyList<string> uploadedKeys, List<string> removedKeys, CancellationToken cancellationToken)
    {
        try
        {
            await fileSaver.SaveAsync(uploadedKeys, removedKeys, cancellationToken);
        }
        catch (DuplicateRecordException exception)
        {
            // A correction in the same moment took the slot: the same answer as when it changed the log first (MSG06).
            throw new ConcurrencyConflictException(exception);
        }
    }
}
