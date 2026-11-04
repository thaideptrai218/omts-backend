using OMTS.Application.Common;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Cultivation.Cycles;
using OMTS.Application.Features.Cultivation.Progress;
using OMTS.Application.Features.Farming.Rafts;
using OMTS.Application.Features.Files;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Harvest;

namespace OMTS.Application.Features.Harvest;

/// <summary>
/// HARVEST-01.0 record a harvest trip to one raft, for a Zone Manager of its zone and the Cooperative Owner, while the raft has an Open cycle. The photos are stored first: if one cannot be
/// uploaded nothing is saved and nobody is told (GFR-01). Everything the save decides is read again after the uploads, which can take minutes at sea: the Open cycle, the lane states, the
/// average kg per cage it copies (BR-HARVEST-02), the harvest time and the lot code. The cycle row is part of the save, so of two parallel saves that change the lane states of a cycle
/// one answers MSG06 and saves nothing, and a new send is checked against the new states. Two lots that take one code without sharing a cycle lose nothing: the loser takes the next code, with its photos.
/// </summary>
public sealed class HarvestLotRecordingService(
    IRaftRepository rafts,
    ICultivationCycleRepository cycles,
    ICultivationCycleQuery cycleQuery,
    IHarvestLotRepository lots,
    ICooperativeRepository cooperatives,
    RaftViews raftViews,
    LaneStateReader laneStates,
    RecordPhotoStore photoStore,
    UploadedFileSaver fileSaver,
    HarvestLotNotifier notifier,
    HarvestLotViews views,
    ZoneAccessGuard zoneAccess,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    TimeProvider timeProvider)
{
    private const int MaxCodeAttempts = 5;

    private sealed record OpenRaft(CultivationCycle Cycle, IReadOnlyDictionary<Guid, LaneState> States, decimal KgPerCage);

    private sealed record PreparedLot(HarvestLot Lot, CultivationCycle Cycle, Raft Raft, bool EverySeededLaneHarvested);

    public async Task<Result<HarvestLotSaved>> RecordAsync(Guid raftId, RecordHarvestRequest request, IReadOnlyList<IncomingFile> photos, CancellationToken cancellationToken)
    {
        // A first look: a request that will be refused stores nothing.
        var raft = await rafts.GetForEditAsync(raftId, cancellationToken);
        if (raft is null)
        {
            return AppError.NotFound;
        }

        if (!await zoneAccess.AllowsAsync(PermissionFunctions.Harvests, PermissionAction.Create, raft.ZoneId, cancellationToken))
        {
            return AppError.Forbidden;
        }

        var (open, refused) = await ReadOpenRaftAsync(raft, timeProvider.Today(), cancellationToken);
        if (refused is not null)
        {
            return refused;
        }

        var (files, photoHint) = HarvestLotFieldReader.ReadPhotos(photos, request.OtherFilePart);
        if (HarvestLotFieldReader.Read(request, raft, open!.States, open.KgPerCage, photoHint, out _) is { } invalid)
        {
            return invalid;
        }

        var recorder = currentUser.AccountId ?? throw new InvalidOperationException("A harvest lot needs an authenticated caller.");
        var uploaded = await photoStore.UploadAllAsync(FileCategories.HarvestLotPhotos, files, cancellationToken);
        if (!uploaded.IsSuccess)
        {
            return uploaded.Error!;
        }

        return await SaveAsync(raftId, request, uploaded.Value!, recorder, cancellationToken);
    }

    /// <summary>One attempt per lot code: a new attempt starts clean, from the database as it now is. The uploads stay until the last attempt fails.</summary>
    private async Task<Result<HarvestLotSaved>> SaveAsync(Guid raftId, RecordHarvestRequest request, IReadOnlyList<string> keys, Guid recorder, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            lots.ForgetTrackedChanges();
            var prepared = await photoStore.PrepareSaveAsync(keys, () => PrepareAsync(raftId, request, keys, recorder, cancellationToken));
            if (!prepared.IsSuccess)
            {
                return prepared.Error!;
            }

            try
            {
                var mayRetry = attempt < MaxCodeAttempts;
                await fileSaver.SaveAsync(keys, [], cancellationToken, exception => mayRetry && exception is DuplicateRecordException);
            }
            catch (DuplicateRecordException) when (attempt < MaxCodeAttempts)
            {
                continue;
            }
            catch (DuplicateRecordException exception)
            {
                throw new ConcurrencyConflictException(exception);
            }

            var done = prepared.Value!;
            var saved = UserMessages.Saved("Harvest lot");
            return new HarvestLotSaved(
                await views.ViewAsync(done.Lot, done.Raft, cancellationToken),
                done.EverySeededLaneHarvested,
                Common.Concurrency.RowVersionGuard.Encode(done.Cycle.RowVersion),
                saved.Text,
                saved.Code);
        }
    }

    private async Task<Result<PreparedLot>> PrepareAsync(Guid raftId, RecordHarvestRequest request, IReadOnlyList<string> keys, Guid recorder, CancellationToken cancellationToken)
    {
        var raft = await rafts.GetForEditAsync(raftId, cancellationToken);
        if (raft is null)
        {
            return AppError.NotFound;
        }

        var now = timeProvider.GetUtcNow();
        var harvestDate = BusinessClock.ToBusinessDate(now);
        var (open, refused) = await ReadOpenRaftAsync(raft, harvestDate, cancellationToken);
        if (refused is not null)
        {
            return refused;
        }

        if (HarvestLotFieldReader.Read(request, raft, open!.States, open.KgPerCage, null, out var fields) is { } invalid)
        {
            return invalid;
        }

        var cycle = open.Cycle;
        var baseCode = HarvestLotCodes.Base(raft.Zone.Code, raft.Code, harvestDate);
        var lot = new HarvestLot
        {
            LotCode = HarvestLotCodes.Next(baseCode, await lots.CodesFromAsync(baseCode, cancellationToken)),
            RaftId = raft.Id,
            Raft = raft,
            CycleId = cycle.Id,
            Cycle = cycle,
            HarvestedAt = now,
            HarvestDate = harvestDate,
            CageCount = fields!.CageCount,
            KgPerCage = open.KgPerCage,
            EstimatedKg = fields.EstimatedKg,
            RecordedByAccountId = recorder,
            Lanes = fields.Lanes.Select(l => new HarvestLotLane { LaneId = l.Id, Lane = l, IsFinished = fields.FinishedLaneIds.Contains(l.Id) }).ToList(),
            Photos = keys.Select((key, index) => new HarvestLotPhoto { SortOrder = index + 1, FileKey = key }).ToList(),
        };
        lots.Add(lot);

        // The lane states depend on the cycle row: a parallel lot, close, cancel or seeding correction read it before this lot, so one of them fails with MSG06.
        unitOfWork.MarkChanged(cycle);
        audit.Record(HarvestLotAudit.Entry(HarvestLotAudit.Created, lot, raft.ZoneId) with { After = HarvestLotAudit.Snapshot(lot) });
        await notifier.LotRecordedAsync(lot, raft, recorder, cancellationToken);

        var everyHarvested = LaneStateRules.EverySeededLaneHarvested(raft.Lanes
            .Where(l => l.StatusId == StatusIds.Lane.InUse)
            .Select(l => fields.FinishedLaneIds.Contains(l.Id) ? LaneState.Harvested : open.States[l.Id]));
        if (everyHarvested && cycle.CloseRemindedAt is null)
        {
            cycle.CloseRemindedAt = now;
            await notifier.CloseReminderAsync(cycle, raft, recorder, cancellationToken);
        }

        return new PreparedLot(lot, cycle, raft, everyHarvested);
    }

    private static AppError NoOpenCycle(Raft raft) => AppError.Conflict(UserMessages.WrongStatus($"Raft {raft.Code}", "without an Open cycle"));

    /// <summary>
    /// MSG-GEN-09 when the raft or its zone is read-only or the raft has no Open cycle; else the tracked cycle, the lanes today and the setting to copy. The cycle row is read before the lane
    /// states: the save then fails if anything changed the cycle since, so the states it decided on are the ones it saves against.
    /// </summary>
    private async Task<(OpenRaft? Open, AppError? Refused)> ReadOpenRaftAsync(Raft raft, DateOnly today, CancellationToken cancellationToken)
    {
        if (raftViews.EditRefusal(raft) is { } readOnly)
        {
            return (null, readOnly);
        }

        var span = await cycleQuery.OpenCycleAsync(raft.Id, cancellationToken);
        var cycle = span is null ? null : await cycles.GetForEditAsync(span.Id, cancellationToken);
        if (cycle is not { StatusId: StatusIds.CultivationCycle.Open })
        {
            return (null, NoOpenCycle(raft));
        }

        var cooperative = await cooperatives.GetCurrentAsync(cancellationToken) ?? throw new InvalidOperationException("A harvest lot needs the cooperative created at deployment.");
        var day = await laneStates.ReadAsync(raft, today, cancellationToken);
        return (new OpenRaft(cycle, day.States, cooperative.AverageKgPerCage), null);
    }
}
