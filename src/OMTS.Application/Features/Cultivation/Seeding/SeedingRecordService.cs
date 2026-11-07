using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Cultivation.Cycles;
using OMTS.Application.Features.Cultivation.Progress;
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
    IRaftRepository rafts,
    CycleWriteGuard writes,
    SeedingSessionReader reader,
    SeedingViews views,
    LaneStateReader laneStates,
    ZoneAccessGuard zoneAccess,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    TimeProvider timeProvider)
{
    public async Task<Result<SeedingRecordSaved>> RecordAsync(Guid cycleId, RecordSeedingRequest request, CancellationToken cancellationToken)
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

        var input = new SeedingInput(request.SeedingDate, request.SeedLotId, request.LaneNumbers, request.Quantity);
        var (fields, invalid) = await reader.ReadAsync(cycle, input, null, null, reasonRequired: false, cancellationToken);
        if (invalid is not null)
        {
            return invalid;
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
            RecordedByAccountId = recorder,
            Lanes = fields.Lanes.Select(l => new SeedingRecordLane { LaneId = l.Id, Lane = l }).ToList(),
        };
        records.Add(record);
        unitOfWork.MarkChanged(cycle);
        unitOfWork.MarkChanged(fields.Lot.Receipt);
        audit.Record(SeedingValues.Entry(SeedingValues.Created, record) with { After = SeedingValues.Snapshot(record) });
        await unitOfWork.SaveChangesAsync(cancellationToken);

        var raft = await rafts.GetForEditAsync(cycle.RaftId, cancellationToken) ?? cycle.Raft;
        return await views.SavedAsync(record, raft, laneStates, timeProvider.Today(), cancellationToken);
    }
}
