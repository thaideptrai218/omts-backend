using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Cultivation.Cycles;
using OMTS.Application.Features.Cultivation.Nursery;
using OMTS.Application.Features.Cultivation.Progress;
using OMTS.Application.Features.Files;
using OMTS.Domain.Entities.Cultivation;

namespace OMTS.Application.Features.Cultivation.Seeding;

/// <summary>
/// CYCLE-02.0 record a seeding session, for a Zone Manager of the raft's zone and the Cooperative Owner, while the cycle is Open. It notifies nobody. The cycle row and the
/// receipt row of the seed lot are part of the save: of a parallel close, cancel, new start date, mark complete or other session, and of a parallel return, cancellation or
/// correction of the receipt, one answers MSG06 with nothing saved, so a return never passes what was seeded (GBR-04).
/// </summary>
public sealed class SeedingRecordService(
    ICultivationCycleRepository cycles,
    ISeedingRecordRepository records,
    INurseryBatchRepository nurseries,
    IRaftRepository rafts,
    CycleWriteGuard writes,
    SeedingSessionReader reader,
    SeedingStockSync stockSync,
    SeedingViews views,
    LaneStateReader laneStates,
    LanePercentGuard percents,
    RecordPhotoStore photoStore,
    UploadedFileSaver fileSaver,
    ZoneAccessGuard zoneAccess,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    TimeProvider timeProvider)
{
    /// <param name="photos">The on-site photos (owner decision of 12/11/2026): 1 to 3, else 400 on <c>Photos</c> and nothing is saved.</param>
    /// <param name="photosRequired">False only for the demo bootstrap, which records sessions without a camera; no request can send it.</param>
    public async Task<Result<SeedingRecordSaved>> RecordAsync(
        Guid cycleId, RecordSeedingRequest request, IReadOnlyList<IncomingFile> photos, CancellationToken cancellationToken, bool photosRequired = true)
    {
        var cycle = await cycles.GetForEditAsync(cycleId, cancellationToken);
        if (cycle is null)
        {
            return AppError.NotFound;
        }

        if (!await zoneAccess.AllowsAsync(PermissionFunctions.CultivationCycles, PermissionAction.Create, cycle.Raft.ZoneId, cancellationToken))
        {
            return AppError.Forbidden;
        }

        if (writes.Refusal(cycle) is { } refused)
        {
            return refused;
        }

        // A cycle that draws from a nursery batch (v2, 13/11/2026) seeds the lot of the batch, any quantity up to what is left in the pool of the batch (every cycle together), with no stock movement
        // (the pieces left the stock when the nursery started) and no plan percentages. The seeding of the cycle must not be marked complete.
        var fromNursery = cycle.NurseryBatchId is not null;
        var batch = fromNursery ? await nurseries.GetForPoolAsync(cycle.NurseryBatchId!.Value, cancellationToken) ?? throw new InvalidOperationException($"Nursery batch {cycle.NurseryBatchId} not found.") : null;
        if (fromNursery && cycle.SeedingCompletedOn is not null)
        {
            return NurseryRules.SeedingCompletedRefusal(cycle.Raft.Code);
        }

        var others = (await records.ListByCycleAsync(cycle.Id, cancellationToken)).Sum(r => r.Quantity);

        var input = new SeedingInput(request.SeedingDate, request.SeedLotId, request.LaneNumbers, request.Quantity, batch?.SeedLotLine);
        var (fields, invalid) = await reader.ReadAsync(cycle, input, null, null, reasonRequired: false, cancellationToken);
        if (invalid is not null)
        {
            return invalid;
        }

        var files = StepPhotoService.Read(photos, otherFilePart: request.OtherFilePart, minimum: photosRequired ? StepPhotoService.MinPhotos : 0);
        if (!files.IsSuccess)
        {
            return files.Error!;
        }

        var shares = percents.ForSeeding(
            await percents.ReadAsync(cycle.Id, cancellationToken), fields!.Lanes, request.LanePercents, request.LanePercentsUnreadable, new Dictionary<Guid, decimal>(), null);
        if (!shares.IsSuccess)
        {
            return shares.Error!;
        }

        string? overPlanReason = null;
        var overPlan = fromNursery
            ? NurseryRules.CheckPool(batch!, (await nurseries.SeededByBatchAsync([batch!.Id], null, cancellationToken)).GetValueOrDefault(batch.Id), fields!.Quantity)
            : SeedingPlanRules.Check(new SeedingPlanInput(cycle.ExpectedSeedQuantity, others, request.ConfirmOverPlan, request.OverPlanReason), fields!.Quantity, out overPlanReason);
        if (overPlan is not null)
        {
            return overPlan;
        }

        var recorder = currentUser.AccountId ?? throw new InvalidOperationException("A seeding session needs an authenticated caller.");
        var record = new SeedingRecord
        {
            CycleId = cycle.Id,
            Cycle = cycle,
            SeedingDate = fields!.Date,
            SeedLotLineId = fields.Lot.Id,
            SeedLotLine = fields.Lot,
            Quantity = fields.Quantity,
            NurseryBatchId = cycle.NurseryBatchId,
            OverPlanReason = overPlanReason,
            RecordedByAccountId = recorder,
            Lanes = fields.Lanes.Select(l => new SeedingRecordLane { LaneId = l.Id, Lane = l, SeededPercent = shares.Value![l.Id] }).ToList(),
        };

        var uploaded = await photoStore.UploadAllAsync(FileCategories.SeedingPhotos, files.Value!, cancellationToken);
        if (!uploaded.IsSuccess)
        {
            return uploaded.Error!;
        }

        // A refusal or a failure from here to the save removes the uploads: no row will point to them.
        var posted = await photoStore.PrepareSaveAsync(uploaded.Value!, async () =>
        {
            if (!fromNursery && await stockSync.PostAsync(record, fields!, cycle.Raft, cancellationToken) is { } stockRefused)
            {
                return Result<bool>.Failure(stockRefused);
            }

            return Result<bool>.Success(true);
        });
        if (!posted.IsSuccess)
        {
            return posted.Error!;
        }

        SeedingPhotoRows.Add(records, record, uploaded.Value!);
        records.Add(record);
        unitOfWork.MarkChanged(cycle);
        if (fromNursery)
        {
            // The pool of the batch is shared by every cycle: two sessions that each fit but not together conflict on the batch row (MSG06).
            unitOfWork.MarkChanged(batch!);
        }
        else
        {
            unitOfWork.MarkChanged(fields.Lot.Receipt);
        }

        audit.Record(SeedingValues.Entry(SeedingValues.Created, record) with { After = SeedingValues.Snapshot(record) });
        await fileSaver.SaveAsync(uploaded.Value!, [], cancellationToken);

        var raft = await rafts.GetForEditAsync(cycle.RaftId, cancellationToken) ?? cycle.Raft;
        return await views.SavedAsync(record, raft, laneStates, timeProvider.Today(), cancellationToken);
    }
}
