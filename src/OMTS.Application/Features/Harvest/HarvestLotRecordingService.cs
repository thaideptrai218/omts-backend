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
using OMTS.Application.Features.Workforce;
using OMTS.Domain.Entities.Identity;
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
/// Since 18/11/2026 a lot is recorded only from a line of a harvest plan (MSG-HARVEST-06) and harvests the raft: every harvestable lane, no lane chosen; <c>allowWithoutPlan</c> is for the demo seed alone. A lot may be recorded from a line of a harvest plan (read again with the rest and moved on in the same save, so two lots of one line never both pass) and may be split into product lines; its cages
/// against the cages the line expects only warn.
/// </summary>
public sealed class HarvestLotRecordingService(
    IRaftRepository rafts,
    ICultivationCycleRepository cycles,
    ICultivationCycleQuery cycleQuery,
    IHarvestLotRepository lots,
    ICooperativeRepository cooperatives,
    RaftViews raftViews,
    LaneStateReader laneStates,
    LanePercentGuard percents,
    RecordPhotoStore photoStore,
    UploadedFileSaver fileSaver,
    HarvestLotNotifier notifier,
    HarvestLotProductLineService productLines,
    HarvestPlanProgress planProgress,
    HarvestLotViews views,
    RecordCrewService crews,
    RecordCrewAssignmentService crewAssignments,
    ZoneAccessGuard zoneAccess,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    TimeProvider timeProvider)
{
    private const int MaxCodeAttempts = 5;

    private sealed record OpenRaft(CultivationCycle Cycle, IReadOnlyDictionary<Guid, LaneState> States, decimal KgPerCage);

    private sealed record PreparedLot(HarvestLot Lot, CultivationCycle Cycle, Raft Raft, bool EverySeededLaneHarvested, decimal? PlanCagesExpected, IReadOnlyList<Farming.FarmingWarning> Warnings, IReadOnlyList<Employee> CrewAdded);

    public async Task<Result<HarvestLotSaved>> RecordAsync(Guid raftId, RecordHarvestRequest request, IReadOnlyList<IncomingFile> photos, CancellationToken cancellationToken, bool allowWithoutPlan = false)
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

        if (planned.Value is null && !allowWithoutPlan)
        {
            return AppError.Conflict(UserMessages.HarvestNeedsPlan);
        }

        if (planned.Value is not null)
        {
            request = WithPlannedLanes(request, raft, open!.States, planned.Value.Line);
        }

        // A new lot needs 1 to 3 on-site photos; a lot recorded before the photos stays valid without.
        var (files, photoHint) = HarvestLotFieldReader.ReadPhotos(photos, request.OtherFilePart, minimum: StepPhotoService.MinPhotos);
        var (resolvedLines, split, lineError) = await WithProductLinesAsync(request, open!.KgPerCage, open.Cycle.ProductId, cancellationToken);
        if (lineError is not null)
        {
            return lineError;
        }

        if (HarvestLotFieldReader.Read(split, raft, open.States, open.KgPerCage, photoHint, out var firstFields) is { } invalid)
        {
            return invalid;
        }

        if ((await SharesAsync(request, firstFields!, open.Cycle.Id, cancellationToken)).Error is { } wrongPercent)
        {
            return wrongPercent;
        }

        if (HarvestProductMatch.Refusal(open.Cycle, resolvedLines) is { } wrongProduct)
        {
            return wrongProduct;
        }

        if ((await crews.ReadAsync(request.Crew, request.CrewUnreadable, cancellationToken)).Error is { } wrongCrew)
        {
            return wrongCrew;
        }

        var recorder = currentUser.AccountId ?? throw new InvalidOperationException("A harvest lot needs an authenticated caller.");
        var uploaded = await photoStore.UploadAllAsync(FileCategories.HarvestLotPhotos, files, cancellationToken);
        if (!uploaded.IsSuccess)
        {
            return uploaded.Error!;
        }

        return await SaveAsync(raftId, request, uploaded.Value!, recorder, allowWithoutPlan, cancellationToken);
    }

    /// <summary>One attempt per lot code: a new attempt starts clean, from the database as it now is. The uploads stay until the last attempt fails.</summary>
    private async Task<Result<HarvestLotSaved>> SaveAsync(Guid raftId, RecordHarvestRequest request, IReadOnlyList<string> keys, Guid recorder, bool allowWithoutPlan, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            // Forgets every row read or added and the SMS messages queued with them, so a new attempt after a lost race on the lot code reads the database again.
            unitOfWork.DiscardChanges();
            var prepared = await photoStore.PrepareSaveAsync(keys, () => PrepareAsync(raftId, request, keys, recorder, allowWithoutPlan, cancellationToken));
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
                saved.Code,
                [.. HarvestQuantityRules.KgWarnings(done.Lot.CageCount, done.Lot.KgPerCage, done.Lot.EstimatedKg), .. done.Warnings],
                done.PlanCagesExpected,
                done.PlanCagesExpected is null ? null : done.Lot.CageCount,
                HarvestPlanCages.Reached(done.PlanCagesExpected, done.Lot.CageCount),
                // A separate step after the save: the lot stays saved whatever the assignment does.
                done.CrewAdded.Count == 0 ? null : await crewAssignments.AssignAsync(done.Lot.HarvestDate, done.Raft.ZoneId, done.Raft.Id, RecordCrewJobs.Harvest, done.CrewAdded, cancellationToken));
        }
    }

    private async Task<Result<PreparedLot>> PrepareAsync(Guid raftId, RecordHarvestRequest request, IReadOnlyList<string> keys, Guid recorder, bool allowWithoutPlan, CancellationToken cancellationToken)
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

        if (planned.Value is null && !allowWithoutPlan)
        {
            return AppError.Conflict(UserMessages.HarvestNeedsPlan);
        }

        if (planned.Value is not null)
        {
            request = WithPlannedLanes(request, raft, open!.States, planned.Value.Line);
        }

        var (resolved, split, lineError) = await WithProductLinesAsync(request, open!.KgPerCage, open.Cycle.ProductId, cancellationToken);
        if (lineError is not null)
        {
            return lineError;
        }

        if (HarvestLotFieldReader.Read(split, raft, open.States, open.KgPerCage, null, out var fields) is { } invalid)
        {
            return invalid;
        }

        var shares = await SharesAsync(request, fields!, open.Cycle.Id, cancellationToken);
        if (!shares.IsSuccess)
        {
            return shares.Error!;
        }

        if (HarvestProductMatch.Refusal(open.Cycle, resolved) is { } wrongProduct)
        {
            return wrongProduct;
        }

        var (crew, crewError) = await crews.ReadAsync(request.Crew, request.CrewUnreadable, cancellationToken);
        if (crewError is not null)
        {
            return crewError;
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
            Lanes = fields.Lanes.Select(l => new HarvestLotLane
            {
                LaneId = l.Id, Lane = l, IsFinished = fields.FinishedLaneIds.Contains(l.Id), HarvestedPercent = shares.Value![l.Id],
            }).ToList(),
            Photos = keys.Select((key, index) => new HarvestLotPhoto { SortOrder = index + 1, FileKey = key }).ToList(),
        };
        lots.Add(lot);
        if (resolved is not null)
        {
            productLines.Add(lot, resolved);
        }

        var crewAdded = crew is null ? [] : crews.Apply(lot.Crew, crew, e => new HarvestRecordEmployee { LotId = lot.Id, EmployeeId = e.Id, Employee = e }).Added;

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

        return new PreparedLot(lot, cycle, raft, everyHarvested, planned.Value?.Line.ExpectedCageCount, HarvestPlanCages.Warnings(planned.Value?.Line, fields.CageCount), crewAdded);
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

    /// <summary>
    /// A lot recorded from a plan line harvests the raft, not chosen lanes (owner decision 18/11/2026): it holds every In Use lane of the raft that can be harvested now (seeded, not damaged or out of service),
    /// whatever lanes the form sent, and finishes none of them (a lane is finished by its own action). The lanes the Zone Manager left out of the line (a plan made before a line was a raft) stay out of the lot.
    /// </summary>
    private static RecordHarvestRequest WithPlannedLanes(RecordHarvestRequest request, Raft raft, IReadOnlyDictionary<Guid, LaneState> states, HarvestPlanLine line)
    {
        var leftOut = line.Lanes.Where(l => l.IsSkipped).Select(l => l.LaneId).ToHashSet();
        return request with
        {
            LaneNumbers = raft.Lanes
                .Where(l => l.StatusId == StatusIds.Lane.InUse && !l.IsDamaged && !l.IsOutOfService && !leftOut.Contains(l.Id) && states.TryGetValue(l.Id, out var state) && LaneStateRules.CanBeHarvested(state))
                .Select(l => l.LaneNumber).Order().ToList(),
            FinishedLaneNumbers = [],
        };
    }

    /// <summary>
    /// The product lines of the form, checked against the catalogue. A lot holds one product, the one farmed in the Open cycle: a line sent without a product takes it, and when the cycle has a product and the form sends
    /// no line, the line is built from the cages and kg of the form. With lines the cages and estimated kg are their sums, whatever the form sent; a cycle with no product and a form with no line is read as before and the lot
    /// is not split per product.
    /// </summary>
    private async Task<(IReadOnlyList<ResolvedProductLine>? Resolved, RecordHarvestRequest Request, AppError? Error)> WithProductLinesAsync(
        RecordHarvestRequest request, decimal kgPerCage, Guid? cycleProductId, CancellationToken cancellationToken)
    {
        var inputs = cycleProductId is { } productId
            ? request.ProductLines?.Select(l => l is { ProductId: null } ? l with { ProductId = productId } : l).ToList() ?? WholeLot(request, productId)
            : request.ProductLines;
        var (resolved, error) = await productLines.ResolveAsync(inputs, request.ProductLinesAreUnreadable, kgPerCage, new HashSet<Guid>(), cancellationToken);
        if (error is not null || resolved is null)
        {
            return (null, request, error);
        }

        var (cages, kg) = HarvestLotProductLineService.Totals(resolved);
        return (resolved, request with { CageCount = cages, EstimatedKg = kg, EstimatedKgIsUnreadable = false }, null);
    }

    /// <summary>The one line of the lot when the form sends none: all its cages and kg are the product of the cycle. Nothing when a value could not be read, so the field check names it.</summary>
    internal static List<HarvestProductLineInput>? WholeLot(RecordHarvestRequest request, Guid cycleProductId) =>
        request is { ProductLinesAreUnreadable: false, EstimatedKgIsUnreadable: false, CageCount: { } cages } ? [new HarvestProductLineInput(cycleProductId, cages, request.EstimatedKg)] : null;

    /// <summary>The share each lane of the new lot takes (<see cref="LanePercentGuard.ForHarvest"/>), checked against what the other lots and the manual changes of the cycle already hold.</summary>
    private async Task<Result<IReadOnlyDictionary<Guid, decimal?>>> SharesAsync(RecordHarvestRequest request, HarvestLotFields fields, Guid cycleId, CancellationToken cancellationToken) =>
        percents.ForHarvest(
            await percents.ReadAsync(cycleId, cancellationToken), fields.Lanes, fields.FinishedLaneIds, request.LanePercents, request.LanePercentsUnreadable,
            new Dictionary<Guid, decimal?>(), null);

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
