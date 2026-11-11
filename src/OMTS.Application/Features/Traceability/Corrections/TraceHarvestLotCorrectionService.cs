using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Files;
using OMTS.Application.Features.Harvest;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Harvest;

namespace OMTS.Application.Features.Traceability.Corrections;

/// <summary>
/// TRACE-03.2 correct the harvest data of a lot of a Closed cycle: its lanes, finished lanes, cages, estimated kg and photos, with the rules of HARVEST-01 and a reason. Never the lot
/// code, the harvest date and time or the kg per cage copied at record time; never removed (BR-TRACE-04). The lanes are read on the close date without the lot's own part, as
/// HARVEST-01.1 reads them today, so a lane added must have had seed by the harvest date (a lane with no seed lot is refused there) and be In Use; the lot's own lanes stay allowed,
/// also one Removed since the close (BR-FARM-04). Only a change of lanes or photos changes the origin information of the lot (BR-ORIGIN-03/04). A Zone Manager confirms the Owner's agreement
/// (MSG-TRACE-04); the Owner only confirms. The lot keeps its origin state and status (BR-TRACE-05, BR-HARVEST-04). The cycle is Closed, so no close reminder is sent. The lot
/// carries a version (NFR-09): everything is checked before the uploads and again after them; the lot and cycle rows are part of the save.
/// </summary>
public sealed class TraceHarvestLotCorrectionService(
    IHarvestLotRepository lots,
    HarvestLotEditReader edits,
    TraceCorrectionGuard guard,
    RecordPhotoStore photoStore,
    UploadedFileSaver fileSaver,
    RowVersionGuard rowVersions,
    HarvestLotViews views,
    AuditLogService audit,
    IUnitOfWork unitOfWork)
{
    private sealed record Checked(HarvestLot Lot, Raft Raft, HarvestLotFields Fields, List<HarvestLotPhoto> Kept, IReadOnlyList<ValidatedFile> Files);

    /// <summary>Steps 5 and 6: the checks of the save but the version, the values before and after, and the lot when it changes. Changes nothing.</summary>
    public async Task<Result<TraceCorrectionPreview>> PreviewAsync(Guid lotId, TraceHarvestLotPreviewRequest request, CancellationToken cancellationToken)
    {
        var lot = await lots.GetAsync(lotId, cancellationToken);
        if (lot is null)
        {
            return AppError.NotFound;
        }

        // The save's meaning, as HARVEST-01.1 and the log corrections read it: a photo left out, or no list at all, is removed. The preview shows it before the save does it.
        var keep = Keys(request.KeepPhotoKeys);
        var fields = new CorrectHarvestLotRequest(request.LaneNumbers, request.FinishedLaneNumbers, request.CageCount, request.EstimatedKg, null, request.Reason, null);
        var seen = await CheckAsync(lot, fields, keep, [], checkVersion: false, cancellationToken);
        if (!seen.IsSuccess)
        {
            return seen.Error!;
        }

        var read = seen.Value!;
        var before = HarvestLotAudit.Snapshot(lot);
        var after = Snapshot(lot, read.Fields, read.Kept.OrderBy(p => p.SortOrder).Select(p => p.FileKey));
        var (_, changedAfter) = AuditDiff.Changed(before, after);
        var affected = Affected(lot, read.Fields, read.Kept, newPhotos: 0);
        return TraceCorrectionAnswers.Preview(
            HarvestLotAudit.RecordType, lot.Id, before, after, changedAfter, affected, await guard.IsOwnerAsync(cancellationToken), RowVersionGuard.Encode(lot.RowVersion));
    }

    /// <param name="ownerApproved">The Zone Manager's answer to MSG-TRACE-04; the Owner never needs it.</param>
    public async Task<Result<TraceHarvestLotCorrected>> CorrectAsync(
        Guid lotId, CorrectHarvestLotRequest request, bool ownerApproved, IReadOnlyList<IncomingFile> newPhotos, CancellationToken cancellationToken)
    {
        var keep = Keys(request.KeepPhotoKeys);
        var lot = await lots.GetForEditAsync(lotId, cancellationToken);
        if (lot is null)
        {
            return AppError.NotFound;
        }

        // A first look: a request that will be refused stores nothing.
        var seen = await CheckAsync(lot, request, keep, newPhotos, checkVersion: true, cancellationToken);
        if (!seen.IsSuccess)
        {
            return seen.Error!;
        }

        var isOwner = await guard.IsOwnerAsync(cancellationToken);
        if (!isOwner && !ownerApproved)
        {
            return TraceCorrectionAnswers.AgreementRequired(Affected(lot, seen.Value!.Fields, seen.Value!.Kept, newPhotos.Count));
        }

        var uploaded = await photoStore.UploadAllAsync(FileCategories.HarvestLotPhotos, seen.Value!.Files, cancellationToken);
        if (!uploaded.IsSuccess)
        {
            return uploaded.Error!;
        }

        var keys = uploaded.Value!;
        unitOfWork.DiscardChanges();
        var applied = await photoStore.PrepareSaveAsync(keys, () => ApplyAsync(lotId, request, keep, keys, !isOwner, cancellationToken));
        if (!applied.IsSuccess)
        {
            return applied.Error!;
        }

        var (done, raft, removedKeys, published) = applied.Value!;
        if (removedKeys is not null)
        {
            await SaveAsync(keys, removedKeys, cancellationToken);
        }

        var saved = UserMessages.Saved("Harvest lot");
        return new TraceHarvestLotCorrected(await views.ViewAsync(done, raft, cancellationToken), published, saved.Text, saved.Code);
    }

    /// <summary>Changes the row as it is after the uploads. The removed keys are null when nothing changed, so nothing is saved.</summary>
    private async Task<Result<(HarvestLot Lot, Raft Raft, List<string>? RemovedKeys, IReadOnlyList<string> Published)>> ApplyAsync(
        Guid lotId, CorrectHarvestLotRequest request, HashSet<string> keep, IReadOnlyList<string> newKeys, bool ownerApprovalConfirmed, CancellationToken cancellationToken)
    {
        var lot = await lots.GetForEditAsync(lotId, cancellationToken);
        if (lot is null)
        {
            return AppError.NotFound;
        }

        var current = await CheckAsync(lot, request, keep, [], checkVersion: true, cancellationToken);
        if (!current.IsSuccess)
        {
            return current.Error!;
        }

        var (_, raft, fields, kept, _) = current.Value!;
        // Worked out before the rows change, as it compares the lot as it is with the correction.
        var affected = Affected(lot, fields, kept, newKeys.Count);
        var before = HarvestLotAudit.Snapshot(lot);
        (lot.CageCount, lot.EstimatedKg) = (fields.CageCount, fields.EstimatedKg);
        HarvestLotCorrectionRows.ApplyLanes(lots, lot, fields);
        var removedKeys = HarvestLotCorrectionRows.ApplyPhotos(lots, lot, kept, newKeys);
        var (changedBefore, changedAfter) = AuditDiff.Changed(before, HarvestLotAudit.Snapshot(lot));
        if (changedAfter is null)
        {
            return (lot, raft, null, []);
        }

        var published = TraceCorrectionAnswers.PublishedCodes(affected);
        // Lanes and photos are rows of their own: a change of them only must still give the lot a new version.
        unitOfWork.MarkChanged(lot);
        unitOfWork.MarkChanged(lot.Cycle);
        audit.Record(HarvestLotAudit.Entry(TraceCorrectionAudit.HarvestLotCorrected, lot, raft.ZoneId) with
        {
            Before = changedBefore, After = TraceCorrectionAudit.After(changedAfter, published, ownerApprovalConfirmed), Reason = request.Reason!.Trim(),
        });
        return (lot, raft, removedKeys, published);
    }

    /// <summary>
    /// The whole check against the rows as they are now: access, the correctable set, a version that is not the current one, the fields, then a version missing or unreadable.
    /// A form read before another save is told so first (NFR-09). The lot comes tracked or not, with its cycle, raft and zone.
    /// </summary>
    private async Task<Result<Checked>> CheckAsync(
        HarvestLot lot, CorrectHarvestLotRequest request, HashSet<string> keep, IReadOnlyList<IncomingFile> newPhotos, bool checkVersion, CancellationToken cancellationToken)
    {
        if (!await guard.MayCorrectAsync(lot.Raft.ZoneId, cancellationToken))
        {
            return AppError.Forbidden;
        }

        if (guard.ClosedCycleRefusal(lot.Cycle, lot.Raft) is { } refused)
        {
            return refused;
        }

        var version = checkVersion ? await rowVersions.CheckAsync(lot, request.RowVersion, cancellationToken) : null;
        if (version is { Kind: ErrorKind.Conflict })
        {
            return version;
        }

        var (raft, facts) = await edits.FactsAsync(lot, cancellationToken, lot.Cycle.CloseDate);
        var kept = lot.Photos.Where(p => keep.Contains(p.FileKey)).ToList();
        var (files, photoHint) = HarvestLotFieldReader.ReadPhotos(newPhotos, request.OtherFilePart, kept.Count, kept.Count == keep.Count);

        // BR-HARVEST-02: left out, the estimate stays, unless the cages changed: then it is worked out again with the kg per cage of the lot, never the current setting.
        var estimatedKg = request.EstimatedKg ?? (request.CageCount == lot.CageCount ? lot.EstimatedKg : null);
        var fields = new RecordHarvestRequest(request.LaneNumbers, request.FinishedLaneNumbers, request.CageCount, estimatedKg, request.EstimatedKgIsUnreadable, request.OtherFilePart);
        // BR-FARM-04: a lane Removed after the close keeps its records, so the lot's own lanes stay allowed whatever their status; only a lane the correction adds must be In Use.
        var ownLaneIds = lot.Lanes.Select(l => l.LaneId).ToHashSet();
        var correction = new HarvestLotCorrectionInput(request.Reason, facts.FinishedHint(request.FinishedLaneNumbers), ownLaneIds, lot.CageCount, lot.EstimatedKg);
        if (HarvestLotFieldReader.Read(fields, raft, facts.Choosable(), lot.KgPerCage, photoHint, out var read, correction) is { } invalid)
        {
            return invalid;
        }

        if (version is not null)
        {
            return version;
        }

        return new Checked(lot, raft, read!, kept, files);
    }

    /// <summary>The corrected lot with the values <see cref="HarvestLotAudit"/> keeps, before it is applied: the code, the harvest time and the kg per cage stay.</summary>
    private static object Snapshot(HarvestLot lot, HarvestLotFields fields, IEnumerable<string> photoKeys) => new
    {
        lot.LotCode,
        lot.HarvestedAt,
        lot.HarvestDate,
        Lanes = fields.Lanes.Select(l => l.LaneNumber).Order().ToList(),
        FinishedLanes = fields.Lanes.Where(l => fields.FinishedLaneIds.Contains(l.Id)).Select(l => l.LaneNumber).Order().ToList(),
        fields.CageCount,
        lot.KgPerCage,
        fields.EstimatedKg,
        lot.OverPlanReason,
        Photos = photoKeys.ToList(),
    };

    /// <summary>
    /// The lot when the correction changes its origin information (MSG-TRACE-04, BR-TRACE-05): its lanes, which bring their seeding dates and seed items, or its photos (BR-ORIGIN-03/04).
    /// The provenance page never shows the cages, the kg or which lanes are finished, so a change of those only names no lot, as a seeding correction of the quantity only does.
    /// </summary>
    /// <param name="newPhotos">How many photos the correction adds.</param>
    private static IReadOnlyList<HarvestLot> Affected(HarvestLot lot, HarvestLotFields fields, List<HarvestLotPhoto> kept, int newPhotos)
    {
        var lanesChange = !lot.Lanes.Select(l => l.LaneId).ToHashSet().SetEquals(fields.Lanes.Select(l => l.Id));
        var photosChange = kept.Count != lot.Photos.Count || newPhotos > 0;
        return lanesChange || photosChange ? [lot] : [];
    }

    // Ordinal, as the key was stored: another spelling is not this file.
    private static HashSet<string> Keys(IReadOnlyList<string?>? keys) => (keys ?? []).Where(k => !string.IsNullOrEmpty(k)).Select(k => k!).ToHashSet(StringComparer.Ordinal);

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
