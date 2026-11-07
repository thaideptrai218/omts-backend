using OMTS.Application.Common;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Cultivation.Cycles;
using OMTS.Application.Features.Cultivation.Progress;
using OMTS.Application.Features.Farming;
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
    IRaftRepository rafts,
    CycleWriteGuard writes,
    SeedingSessionReader reader,
    SeedingViews views,
    LaneStateReader laneStates,
    RowVersionGuard rowVersions,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    public async Task<Result<SeedingRecordSaved>> CorrectAsync(Guid recordId, CorrectSeedingRequest request, CancellationToken cancellationToken)
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

        var input = new SeedingInput(request.SeedingDate, request.SeedLotId, request.LaneNumbers, request.Quantity);
        var (fields, invalid) = await reader.ReadAsync(record.Cycle, input, record, request.Reason, reasonRequired: true, cancellationToken);
        if (invalid is not null)
        {
            return invalid;
        }

        if (await rowVersions.CheckAsync(record, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        var before = SeedingValues.Snapshot(record);
        SeedingRecordRows.Apply(records, record, fields!);
        var (changedBefore, changedAfter) = AuditDiff.Changed(before, SeedingValues.Snapshot(record));
        if (changedAfter is not null)
        {
            // Lanes are rows of their own: a change of lanes only must still give the session a new version.
            unitOfWork.MarkChanged(record);
            unitOfWork.MarkChanged(record.Cycle);
            unitOfWork.MarkChanged(fields!.Lot.Receipt);
            audit.Record(SeedingValues.Entry(SeedingValues.Corrected, record) with { Before = changedBefore, After = changedAfter, Reason = fields.Reason });
            await unitOfWork.SaveChangesAsync(cancellationToken);
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
