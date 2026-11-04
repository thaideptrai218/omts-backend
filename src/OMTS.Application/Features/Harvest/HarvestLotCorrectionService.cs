using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Files;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Harvest;

namespace OMTS.Application.Features.Harvest;

/// <summary>
/// HARVEST-01.1 correct a lot with a reason while its cycle is Open (BR-HARVEST-05): a Zone Manager what they recorded, the Cooperative Owner any lot, also one on an invoice. The Owner is not told again.
/// The lot code, the harvest date and time and the kg per cage copied at record time never change. A lot carries a version (NFR-09): everything is checked before the uploads, so a refused request stores
/// nothing, and again after them, which can take minutes at sea. The cycle row is part of the save, as the lane states depend on it. A Published lot shows the correction at once, and the audit row names it (BR-TRACE-05).
/// </summary>
public sealed class HarvestLotCorrectionService(
    IHarvestLotRepository lots,
    HarvestLotEditReader edits,
    RecordPhotoStore photoStore,
    UploadedFileSaver fileSaver,
    RowVersionGuard rowVersions,
    HarvestLotNotifier notifier,
    HarvestLotViews views,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    TimeProvider timeProvider)
{
    private sealed record Checked(HarvestLot Lot, Raft Raft, HarvestLotFields Fields, HarvestLotLaneFacts Facts, List<HarvestLotPhoto> Kept, IReadOnlyList<ValidatedFile> Files);

    private sealed record Applied(HarvestLot Lot, Raft Raft, List<string> RemovedKeys, bool Changed, bool EveryHarvested);

    public async Task<Result<HarvestLotSaved>> CorrectAsync(Guid lotId, CorrectHarvestLotRequest request, IReadOnlyList<IncomingFile> newPhotos, CancellationToken cancellationToken)
    {
        // Ordinal, as the key was stored: another spelling is not this file.
        var keep = (request.KeepPhotoKeys ?? []).Where(k => !string.IsNullOrEmpty(k)).Select(k => k!).ToHashSet(StringComparer.Ordinal);

        // A first look: a request that will be refused stores nothing.
        var seen = await CheckAsync(lotId, request, keep, newPhotos, cancellationToken);
        if (!seen.IsSuccess)
        {
            return seen.Error!;
        }

        var uploaded = await photoStore.UploadAllAsync(FileCategories.HarvestLotPhotos, seen.Value!.Files, cancellationToken);
        if (!uploaded.IsSuccess)
        {
            return uploaded.Error!;
        }

        var keys = uploaded.Value!;
        lots.ForgetTrackedChanges();
        var applied = await photoStore.PrepareSaveAsync(keys, () => ApplyAsync(lotId, request, keep, keys, cancellationToken));
        if (!applied.IsSuccess)
        {
            return applied.Error!;
        }

        var done = applied.Value!;
        if (done.Changed)
        {
            await SaveAsync(keys, done.RemovedKeys, cancellationToken);
        }

        var saved = UserMessages.Saved("Harvest lot");
        return new HarvestLotSaved(await views.ViewAsync(done.Lot, done.Raft, cancellationToken), done.EveryHarvested, RowVersionGuard.Encode(done.Lot.Cycle.RowVersion), saved.Text, saved.Code);
    }

    /// <summary>The whole check against the rows as they are now, in the order of the record: access, the state of the cycle, the fields, then the version.</summary>
    private async Task<Result<Checked>> CheckAsync(
        Guid lotId, CorrectHarvestLotRequest request, HashSet<string> keep, IReadOnlyList<IncomingFile> newPhotos, CancellationToken cancellationToken)
    {
        var lot = await lots.GetForEditAsync(lotId, cancellationToken);
        if (lot is null)
        {
            return AppError.NotFound;
        }

        if (await edits.RefusalAsync(lot, cancellationToken) is { } refused)
        {
            return refused;
        }

        var (raft, facts) = await edits.FactsAsync(lot, cancellationToken);
        var kept = lot.Photos.Where(p => keep.Contains(p.FileKey)).ToList();
        var (files, photoHint) = HarvestLotFieldReader.ReadPhotos(newPhotos, request.OtherFilePart, kept.Count, kept.Count == keep.Count);

        // BR-HARVEST-02: left out, the estimate stays, unless the cages changed: then it is worked out again with the kg per cage of the lot, never the current setting.
        var estimatedKg = request.EstimatedKg ?? (request.CageCount == lot.CageCount ? lot.EstimatedKg : null);
        var fields = new RecordHarvestRequest(request.LaneNumbers, request.FinishedLaneNumbers, request.CageCount, estimatedKg, request.EstimatedKgIsUnreadable, request.OtherFilePart);
        var correction = new HarvestLotCorrectionInput(request.Reason, facts.FinishedHint(request.FinishedLaneNumbers));
        if (HarvestLotFieldReader.Read(fields, raft, facts.Choosable(), lot.KgPerCage, photoHint, out var read, correction) is { } invalid)
        {
            return invalid;
        }

        if (await rowVersions.CheckAsync(lot, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        return new Checked(lot, raft, read!, facts, kept, files);
    }

    /// <summary>Changes the row as it is after the uploads. The cap on photos still holds, as the kept photos can only be the same ones.</summary>
    private async Task<Result<Applied>> ApplyAsync(Guid lotId, CorrectHarvestLotRequest request, HashSet<string> keep, IReadOnlyList<string> newKeys, CancellationToken cancellationToken)
    {
        var current = await CheckAsync(lotId, request, keep, [], cancellationToken);
        if (!current.IsSuccess)
        {
            return current.Error!;
        }

        var (lot, raft, fields, facts, kept, _) = current.Value!;
        var everyBefore = facts.EveryHarvestedNow();
        var everyAfter = facts.EveryHarvestedWith(fields);
        var before = HarvestLotAudit.Snapshot(lot);
        (lot.CageCount, lot.EstimatedKg) = (fields.CageCount, fields.EstimatedKg);
        ApplyLanes(lot, fields);
        var removedKeys = ApplyPhotos(lot, kept, newKeys);
        var (changedBefore, changedAfter) = AuditDiff.Changed(before, HarvestLotAudit.Snapshot(lot));
        if (changedAfter is null)
        {
            return new Applied(lot, raft, removedKeys, false, everyAfter);
        }

        // Lanes and photos are rows of their own: a change of them only must still give the lot a new version.
        unitOfWork.MarkChanged(lot);
        unitOfWork.MarkChanged(lot.Cycle);
        audit.Record(HarvestLotAudit.Entry(HarvestLotAudit.Corrected, lot, raft.ZoneId) with { Before = changedBefore, After = HarvestLotAudit.WithAffectedPublishedLot(changedAfter, lot), Reason = request.Reason!.Trim() });
        await RemindAsync(lot, raft, everyBefore, everyAfter, cancellationToken);
        return new Applied(lot, raft, removedKeys, true, everyAfter);
    }

    /// <summary>
    /// HARVEST-01.2 at step 8 of a correction: the last seeded lane marked Harvested reminds, once while the lanes stay so. A correction that takes a lane out of Harvested re-arms it, as
    /// the earlier reminder is no longer true and BR-CYCLE-07 reminds when the last lane is marked. MSG-HARVEST-01 is not sent again, but this one is another notice.
    /// </summary>
    private async Task RemindAsync(HarvestLot lot, Raft raft, bool everyBefore, bool everyAfter, CancellationToken cancellationToken)
    {
        if (everyBefore && !everyAfter)
        {
            lot.Cycle.CloseRemindedAt = null;
        }
        else if (!everyBefore && everyAfter && lot.Cycle.CloseRemindedAt is null)
        {
            lot.Cycle.CloseRemindedAt = timeProvider.GetUtcNow();
            var recorder = currentUser.AccountId ?? throw new InvalidOperationException("A correction needs an authenticated caller.");
            await notifier.CloseReminderAsync(lot.Cycle, raft, recorder, cancellationToken);
        }
    }

    private void ApplyLanes(HarvestLot lot, HarvestLotFields fields)
    {
        var wanted = fields.Lanes.ToDictionary(l => l.Id);
        foreach (var lane in lot.Lanes.Where(l => !wanted.ContainsKey(l.LaneId)).ToList())
        {
            lot.Lanes.Remove(lane);
            lots.RemoveLane(lane);
        }

        foreach (var lane in lot.Lanes)
        {
            lane.IsFinished = fields.FinishedLaneIds.Contains(lane.LaneId);
        }

        foreach (var lane in fields.Lanes.Where(l => lot.Lanes.All(existing => existing.LaneId != l.Id)))
        {
            var added = new HarvestLotLane { LotId = lot.Id, LaneId = lane.Id, Lane = lane, IsFinished = fields.FinishedLaneIds.Contains(lane.Id) };
            lot.Lanes.Add(added);
            lots.AddLane(added);
        }
    }

    /// <summary>Removes the photos not kept and adds the new ones in the free slots; kept photos stay in their slots. Returns the keys of the removed ones.</summary>
    private List<string> ApplyPhotos(HarvestLot lot, List<HarvestLotPhoto> kept, IReadOnlyList<string> newKeys)
    {
        var removed = lot.Photos.Except(kept).ToList();
        foreach (var photo in removed)
        {
            lot.Photos.Remove(photo);
            lots.RemovePhoto(photo);
        }

        using var freeSlots = Enumerable.Range(1, HarvestLot.MaxPhotos).Where(slot => kept.All(p => p.SortOrder != slot)).GetEnumerator();
        foreach (var key in newKeys)
        {
            freeSlots.MoveNext();
            var photo = new HarvestLotPhoto { LotId = lot.Id, SortOrder = freeSlots.Current, FileKey = key };
            lot.Photos.Add(photo);
            lots.AddPhoto(photo);
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
            // A correction in the same moment took the slot or the lane: the same answer as when it changed the lot first (MSG06).
            throw new ConcurrencyConflictException(exception);
        }
    }
}
