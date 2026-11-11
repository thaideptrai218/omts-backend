using OMTS.Application.Common;
using OMTS.Application.Common.Concurrency;
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
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Harvest;

namespace OMTS.Application.Features.Harvest;

/// <summary>The answer of "finish harvest" on a lane.</summary>
/// <param name="Changed">False when the lane was already Harvested: nothing was written (the call is idempotent).</param>
/// <param name="State">The lane state now: always <see cref="LaneState.Harvested"/>.</param>
/// <param name="LotCode">The lot whose lane row carries the finish mark.</param>
/// <param name="EverySeededLaneHarvested">Every seeded lane of the cycle shows Harvested now: the cycle can be closed normally.</param>
/// <param name="CycleRowVersion">The new version of the cycle.</param>
public sealed record LaneHarvestFinished(
    int LaneNumber, LaneState State, bool Changed, string LotCode, bool EverySeededLaneHarvested, string CycleRowVersion, string Message, string MessageCode);

/// <summary>
/// HARVEST-01 step 3 on its own (PM decision 11/11/2026): marks a lane Being harvested as finished without editing a lot. The mark goes on the lane row of the latest lot of the cycle that
/// includes the lane, so the lane states of <see cref="LaneStateRules"/> are unchanged. Same access as recording a harvest; audited; the cycle row is part of the save, as for a lot.
/// A lane already Harvested answers 200 with <c>changed: false</c>; a lane with no harvest lot to finish answers 409 MSG-GEN-09.
/// </summary>
public sealed class HarvestLaneFinishService(
    IRaftRepository rafts,
    ICultivationCycleRepository cycles,
    ICultivationCycleQuery cycleQuery,
    IHarvestLotRepository lots,
    RaftViews raftViews,
    LaneStateReader laneStates,
    HarvestLotNotifier notifier,
    ZoneAccessGuard zoneAccess,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    TimeProvider timeProvider)
{
    public const string LaneFinished = "HarvestLot.LaneFinished";

    public async Task<Result<LaneHarvestFinished>> FinishAsync(Guid raftId, int laneNumber, CancellationToken cancellationToken)
    {
        var raft = await rafts.GetForEditAsync(raftId, cancellationToken);
        if (raft is null)
        {
            return AppError.NotFound;
        }

        if (!await zoneAccess.AllowsAsync(PermissionFunctions.Harvests, PermissionAction.Create, raft.ZoneId, cancellationToken))
        {
            return AppError.Forbidden;
        }

        var lane = raft.Lanes.FirstOrDefault(l => l.LaneNumber == laneNumber && l.StatusId == StatusIds.Lane.InUse);
        if (lane is null)
        {
            return AppError.NotFound;
        }

        if (raftViews.EditRefusal(raft) is { } readOnly)
        {
            return readOnly;
        }

        var span = await cycleQuery.OpenCycleAsync(raft.Id, cancellationToken);
        var cycle = span is null ? null : await cycles.GetForEditAsync(span.Id, cancellationToken);
        if (cycle is not { StatusId: StatusIds.CultivationCycle.Open })
        {
            return AppError.Conflict(UserMessages.WrongStatus($"Raft {raft.Code}", "without an Open cycle"));
        }

        // The cycle row is read before the lane states: the save fails if anything changed the cycle since.
        var states = (await laneStates.ReadAsync(raft, timeProvider.Today(), cancellationToken)).States;
        var inLots = await lots.ListOfLaneAsync(cycle.Id, lane.Id, cancellationToken);
        var latest = inLots.OrderByDescending(l => l.Lot.HarvestedAt).FirstOrDefault();
        if (latest is null || states[lane.Id] is not (LaneState.BeingHarvested or LaneState.Harvested))
        {
            return AppError.Conflict(UserMessages.WrongStatus($"Lane {laneNumber} of raft {raft.Code}", "not being harvested: it has no harvest lot to finish"));
        }

        var saved = UserMessages.Saved("Lane harvest");
        var finishedBy = inLots.FirstOrDefault(l => l.IsFinished) ?? latest;
        if (states[lane.Id] == LaneState.Harvested)
        {
            return new LaneHarvestFinished(laneNumber, LaneState.Harvested, false, finishedBy.Lot.LotCode, EveryHarvested(raft, states), RowVersionGuard.Encode(cycle.RowVersion), saved.Text, saved.Code);
        }

        // Tracked copy of the lot, to change its lane row.
        var lot = await lots.GetForEditAsync(latest.LotId, cancellationToken) ?? throw new InvalidOperationException("The lot of a lane row must exist.");
        var row = lot.Lanes.Single(l => l.LaneId == lane.Id);
        row.IsFinished = true;
        unitOfWork.MarkChanged(lot);
        unitOfWork.MarkChanged(cycle);
        audit.Record(HarvestLotAudit.Entry(LaneFinished, lot, raft.ZoneId) with
        {
            Before = new { Lane = laneNumber, IsFinished = false },
            After = new { Lane = laneNumber, IsFinished = true },
        });

        var after = new Dictionary<Guid, LaneState>(states) { [lane.Id] = LaneState.Harvested };
        var every = EveryHarvested(raft, after);
        if (every && cycle.CloseRemindedAt is null)
        {
            var actor = currentUser.AccountId ?? throw new InvalidOperationException("A lane finish needs an authenticated caller.");
            cycle.CloseRemindedAt = timeProvider.GetUtcNow();
            await notifier.CloseReminderAsync(cycle, raft, actor, cancellationToken);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new LaneHarvestFinished(laneNumber, LaneState.Harvested, true, lot.LotCode, every, RowVersionGuard.Encode(cycle.RowVersion), saved.Text, saved.Code);
    }

    private static bool EveryHarvested(Raft raft, IReadOnlyDictionary<Guid, LaneState> states) =>
        LaneStateRules.EverySeededLaneHarvested(raft.Lanes.Where(l => l.StatusId == StatusIds.Lane.InUse).Select(l => states[l.Id]));
}
