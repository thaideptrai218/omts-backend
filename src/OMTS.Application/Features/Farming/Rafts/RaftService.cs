using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Application.Features.Farming.Rafts;

/// <summary>
/// FARM-04 create and edit of rafts and their lanes, Cooperative Owner only (API filter). A Discontinued raft is read-only
/// until it is resumed (GBR-01), and the number of lanes changes only while no cultivation cycle is open (BR-FARM-04).
/// </summary>
public sealed class RaftService(
    IRaftRepository rafts,
    IFarmingZoneRepository zones,
    ICultivationCycleQuery cycles,
    RaftViews views,
    IStatusCatalog statusCatalog,
    RowVersionGuard rowVersions,
    AuditLogService audit,
    IUnitOfWork unitOfWork)
{
    private const string RecordType = nameof(Raft);

    public async Task<Result<RaftSaved>> CreateAsync(Guid zoneId, SaveRaftRequest request, CancellationToken cancellationToken)
    {
        var zone = await zones.GetForEditAsync(zoneId, cancellationToken);
        if (zone is null)
        {
            return AppError.NotFound;
        }

        // FARM-04.0.E3: no raft is added to a Discontinued zone (MSG-GEN-09 naming the zone).
        if (zone.StatusId != StatusIds.Usage.InUse)
        {
            return AppError.NotUsableChoice($"Farming zone {zone.Code}", statusCatalog.DisplayNameOf(zone.StatusId), "Zone");
        }

        // The zone must still be In Use when this save commits: a parallel discontinue then fails with MSG06.
        unitOfWork.MarkChanged(zone);
        var check = RaftValues.Read(request, out var values);
        if (!check.IsValid)
        {
            return check.ToError();
        }

        if (await rafts.FindByCodeAsync(zone.Id, values.Code, null, cancellationToken) is { } existing)
        {
            return Duplicate(existing.Id);
        }

        var raft = new Raft { ZoneId = zone.Id, Zone = zone };
        values.ApplyTo(raft);
        foreach (var number in Enumerable.Range(1, values.LaneCount))
        {
            raft.Lanes.Add(new Lane { RaftId = raft.Id, LaneNumber = number });
        }

        rafts.Add(raft);
        audit.Record(new AuditEntry("Raft.Created", RecordType)
        {
            RecordId = raft.Id, RecordCode = raft.Code, ZoneId = raft.ZoneId, After = new { Raft = RaftValues.Snapshot(raft), values.LaneCount },
        });
        return await SaveNewAsync(raft, cancellationToken) ?? (Result<RaftSaved>)await views.SavedAsync(raft, "Raft", cancellationToken);
    }

    /// <summary>
    /// Saves a new raft with its zone row, so a parallel create of the same code usually loses on the row version of the
    /// zone before it reaches the unique index. That loser answers like the pre-check too (MSG-GEN-02 with the existing
    /// raft, FARM-04.0.E1); with no such raft, the zone itself changed meanwhile (MSG06). Null when saved.
    /// </summary>
    private async Task<AppError?> SaveNewAsync(Raft raft, CancellationToken cancellationToken)
    {
        try
        {
            return await SaveAsync(raft, null, cancellationToken);
        }
        catch (ConcurrencyConflictException)
        {
            if (await rafts.FindByCodeAsync(raft.ZoneId, raft.Code, null, cancellationToken) is { } existing)
            {
                return Duplicate(existing.Id);
            }

            throw;
        }
    }

    /// <summary>
    /// FARM-04.2: edits an In Use raft (MSG-GEN-09 otherwise). A new number of lanes is refused with MSG-GEN-06 while a cycle is
    /// open and nothing is saved: the client keeps the entries and may save the other edits with the old number (E1).
    /// </summary>
    public async Task<Result<RaftSaved>> UpdateAsync(Guid raftId, SaveRaftRequest request, CancellationToken cancellationToken)
    {
        var raft = await rafts.GetForEditAsync(raftId, cancellationToken);
        if (raft is null)
        {
            return AppError.NotFound;
        }

        if (views.EditRefusal(raft) is { } refused)
        {
            return refused;
        }

        var check = RaftValues.Read(request, out var values);
        if (!check.IsValid)
        {
            return check.ToError();
        }

        if (await rafts.FindByCodeAsync(raft.ZoneId, values.Code, raft.Id, cancellationToken) is { } existing)
        {
            return Duplicate(existing.Id);
        }

        if (await rowVersions.CheckAsync(raft, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        if (RaftLaneChange.InUseCount(raft.Lanes) != values.LaneCount)
        {
            if (await cycles.OpenCycleAsync(raft.Id, cancellationToken) is { } cycle)
            {
                return RaftStatusService.OpenCycle(cycle);
            }

            ChangeLanes(raft, values.LaneCount);
        }

        var before = RaftValues.Snapshot(raft);
        values.ApplyTo(raft);
        var (changedBefore, changedAfter) = AuditDiff.Changed(before, RaftValues.Snapshot(raft));
        if (changedAfter is not null)
        {
            audit.Record(new AuditEntry("Raft.Updated", RecordType)
            {
                RecordId = raft.Id, RecordCode = raft.Code, ZoneId = raft.ZoneId, Before = changedBefore, After = changedAfter,
            });
        }

        return await SaveAsync(raft, raft.Id, cancellationToken) ?? (Result<RaftSaved>)await views.SavedAsync(raft, "Raft", cancellationToken);
    }

    private void ChangeLanes(Raft raft, int laneCount)
    {
        var before = RaftLaneChange.InUseCount(raft.Lanes);
        var change = RaftLaneChange.To(laneCount, raft.Lanes);
        foreach (var lane in change.Removed)
        {
            lane.StatusId = StatusIds.Lane.Removed;
        }

        foreach (var lane in change.Restored)
        {
            lane.StatusId = StatusIds.Lane.InUse;
        }

        foreach (var number in change.NewNumbers)
        {
            var lane = new Lane { RaftId = raft.Id, LaneNumber = number };
            raft.Lanes.Add(lane);
            rafts.AddLane(lane);
        }

        // Lanes are rows of their own: the raft must still get a new row version (MSG06 for stale tabs, or a cycle started meanwhile).
        unitOfWork.MarkChanged(raft);
        audit.Record(new AuditEntry("Raft.LanesChanged", RecordType)
        {
            RecordId = raft.Id,
            RecordCode = raft.Code,
            ZoneId = raft.ZoneId,
            Before = new { LaneCount = before },
            After = new
            {
                LaneCount = laneCount,
                RemovedLanes = change.Removed.Select(l => l.LaneNumber).ToList(),
                RestoredLanes = change.Restored.Select(l => l.LaneNumber).ToList(),
                NewLanes = change.NewNumbers,
            },
        });
    }

    /// <summary>
    /// Saves the raft. A parallel save of the same code stopped by the unique index answers what the pre-check answers
    /// (MSG-GEN-02 with the existing raft, FARM-04.0.E1); with no such raft, the index was the lane number taken by a
    /// parallel lane change of this raft (MSG06). Null when saved.
    /// </summary>
    private async Task<AppError?> SaveAsync(Raft raft, Guid? exceptRaftId, CancellationToken cancellationToken)
    {
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return null;
        }
        catch (DuplicateRecordException exception)
        {
            return await rafts.FindByCodeAsync(raft.ZoneId, raft.Code, exceptRaftId, cancellationToken) is { } existing
                ? Duplicate(existing.Id)
                : throw new ConcurrencyConflictException(exception);
        }
    }

    private static AppError Duplicate(Guid existingId) => new(
        ErrorKind.Conflict,
        UserMessages.AlreadyExists("Raft"),
        new Dictionary<string, object?> { ["existingId"] = existingId, ["existingRaftId"] = existingId });
}
