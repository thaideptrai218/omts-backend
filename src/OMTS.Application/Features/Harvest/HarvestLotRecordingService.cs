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
using OMTS.Application.Features.Harvest.Plans;
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
    HarvestLotProductLineService productLines,
    HarvestPlanProgress planProgress,
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

        var planned = await ResolvePlanAsync(request, raftId, cancellationToken);
        if (!planned.IsSuccess)
        {
            return planned.Error!;
        }

        request = WithPlannedLanes(request, planned.Value);
        var (files, photoHint) = HarvestLotFieldReader.ReadPhotos(photos, request.OtherFilePart);
        var (_, split, lineError) = await WithProductLinesAsync(request, open!.KgPerCage, open.Cycle.ProductId, cancellationToken);
        if (lineError is not null)
        {
            return lineError;
        }

        if (HarvestLotFieldReader.Read(split, raft, open.States, open.KgPerCage, photoHint, out _) is { } invalid)
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
            // Forgets every row read or added and the pushes queued with them, so a new attempt after a lost race on the lot code reads the database again.
            unitOfWork.DiscardChanges();
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

        var planned = await ResolvePlanAsync(request, raftId, cancellationToken);
        if (!planned.IsSuccess)
        {
            return planned.Error!;
        }

        request = WithPlannedLanes(request, planned.Value);
        var (resolved, split, lineError) = await WithProductLinesAsync(request, open!.KgPerCage, open.Cycle.ProductId, cancellationToken);
        if (lineError is not null)
        {
            return lineError;
        }

        if (HarvestLotFieldReader.Read(split, raft, open.States, open.KgPerCage, null, out var fields) is { } invalid)
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
        if (resolved is not null)
        {
            productLines.Add(lot, raft.Code, resolved);
        }

        // The lot carries the link to its line of the plan, and the plan moves on in the same save (Harvesting, then Completed when every line is done).
        if (planned.Value is { } plannedHarvest)
        {
            planProgress.OnLotRecorded(plannedHarvest, lot);
        }

        // The lane states depend on the cycle row: a parallel lot, close, cancel or seeding correction read it before this lot, so one of them fails with MSG06.
        unitOfWork.MarkChanged(cycle);
        audit.Record(HarvestLotAudit.Entry(HarvestLotAudit.Created, lot, raft.ZoneId) with { After = HarvestLotAudit.Snapshot(lot) });
        await notifier.LotRecordedAsync(lot, raft, recorder, cancellationToken, planned.Value?.Plan.Code);

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

    /// <summary>The line of the plan the lot is recorded from (none when the form names none); the plan is read again on every attempt, so a line recorded or a lane left out meanwhile is told.</summary>
    private async Task<Result<PlannedHarvest?>> ResolvePlanAsync(RecordHarvestRequest request, Guid raftId, CancellationToken cancellationToken)
    {
        if (request.HarvestPlanLineIsUnreadable)
        {
            return AppError.Invalid(HarvestPlanProgress.FieldName);
        }

        if (request.HarvestPlanLineId is not { } lineId)
        {
            return (PlannedHarvest?)null;
        }

        var resolved = await planProgress.ResolveLineAsync(lineId, raftId, cancellationToken);
        return resolved.IsSuccess ? resolved.Value : resolved.Error!;
    }

    /// <summary>A lot recorded from a plan line holds the lanes of the plan without those left out, whatever lanes the form sent.</summary>
    private static RecordHarvestRequest WithPlannedLanes(RecordHarvestRequest request, PlannedHarvest? planned) =>
        planned is null ? request : request with { LaneNumbers = planned.LaneNumbers };

    /// <summary>
    /// The product lines of the form, checked against the catalogue. With lines the cages and estimated kg are their sums, whatever the form sent; without them the form is read as before and the lot is
    /// not split per product. A line sent without a product takes the product farmed in the Open cycle.
    /// </summary>
    private async Task<(IReadOnlyList<ResolvedProductLine>? Resolved, RecordHarvestRequest Request, AppError? Error)> WithProductLinesAsync(
        RecordHarvestRequest request, decimal kgPerCage, Guid? cycleProductId, CancellationToken cancellationToken)
    {
        var inputs = cycleProductId is { } productId
            ? request.ProductLines?.Select(l => l is { ProductId: null } ? l with { ProductId = productId } : l).ToList()
            : request.ProductLines;
        var (resolved, error) = await productLines.ResolveAsync(inputs, request.ProductLinesAreUnreadable, kgPerCage, new HashSet<Guid>(), cancellationToken);
        if (error is not null || resolved is null)
        {
            return (null, request, error);
        }

        var (cages, kg) = HarvestLotProductLineService.Totals(resolved);
        return (resolved, request with { CageCount = cages, EstimatedKg = kg, EstimatedKgIsUnreadable = false }, null);
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
