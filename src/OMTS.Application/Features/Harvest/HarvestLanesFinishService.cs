using OMTS.Application.Common;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Cultivation.Cycles;
using OMTS.Application.Features.Cultivation.Progress;
using OMTS.Application.Features.Farming.Rafts;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Harvest;

namespace OMTS.Application.Features.Harvest;

/// <summary>Body of "finish harvest" on many lanes. <c>RowVersion</c> is the cycle version the screen read; optional, as the single-lane call has none.</summary>
public sealed record FinishLanesRequest(IReadOnlyList<int>? LaneNumbers, string? RowVersion);

/// <summary>One lane that blocks the whole call.</summary>
public sealed record LaneFinishProblem(int LaneNumber, string ReasonCode, string Message);

/// <param name="Finished">Lanes finished by this call.</param>
/// <param name="AlreadyFinished">Lanes that were already Harvested: nothing written for them.</param>
public sealed record LanesHarvestFinished(
    IReadOnlyList<int> Finished, IReadOnlyList<int> AlreadyFinished, bool EverySeededLaneHarvested, string CycleRowVersion, string Message, string MessageCode);

/// <summary>
/// "Finish harvest" on several lanes at once (owner decision 12/11/2026). All or nothing: every lane must follow the rule of <see cref="HarvestLaneFinishService"/>, else 409 MSG-GEN-09
/// lists the offending lanes in <c>lanes</c> and nothing is saved. Lanes already Harvested are only reported (idempotent). One audit row per lane finished plus one summary row on the raft;
/// the close reminder is sent once, when the last seeded lane is finished. Same access as the single call.
/// </summary>
public sealed class HarvestLanesFinishService(
    IRaftRepository rafts,
    ICultivationCycleRepository cycles,
    ICultivationCycleQuery cycleQuery,
    IHarvestLotRepository lots,
    RaftViews raftViews,
    LaneStateReader laneStates,
    HarvestLotNotifier notifier,
    ZoneAccessGuard zoneAccess,
    RowVersionGuard rowVersions,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    TimeProvider timeProvider)
{
    public const int MaxLanes = 200;
    public const string LanesFinished = "Raft.HarvestLanesFinished";
    public const string LaneNotFound = "LaneNotFound";
    public const string NotBeingHarvested = "NotBeingHarvested";

    public async Task<Result<LanesHarvestFinished>> FinishAsync(Guid raftId, FinishLanesRequest request, CancellationToken cancellationToken)
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

        var numbers = request.LaneNumbers;
        if (numbers is not { Count: > 0 and <= MaxLanes } || numbers.Any(n => n < 1) || numbers.Distinct().Count() != numbers.Count)
        {
            return AppError.Invalid("LaneNumbers", $"1 to {MaxLanes} distinct lane numbers");
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

        if (request.RowVersion is not null && await rowVersions.CheckAsync(cycle, request.RowVersion, cancellationToken) is { } stale)
        {
            return stale;
        }

        var states = (await laneStates.ReadAsync(raft, timeProvider.Today(), cancellationToken)).States;
        var problems = new List<LaneFinishProblem>();
        var already = new List<int>();
        var toFinish = new List<(Lane Lane, int Number, HarvestLotLane Latest)>();
        foreach (var number in numbers.Order())
        {
            var lane = raft.Lanes.FirstOrDefault(l => l.LaneNumber == number && l.StatusId == StatusIds.Lane.InUse);
            if (lane is null)
            {
                problems.Add(new LaneFinishProblem(number, LaneNotFound, $"Làn {number} không có trên bè này."));
                continue;
            }

            var inLots = await lots.ListOfLaneAsync(cycle.Id, lane.Id, cancellationToken);
            var latest = inLots.OrderByDescending(l => l.Lot.HarvestedAt).FirstOrDefault();
            if (latest is null || states[lane.Id] is not (LaneState.BeingHarvested or LaneState.Harvested))
            {
                problems.Add(new LaneFinishProblem(number, NotBeingHarvested, $"Làn {number} chưa có đợt thu hoạch nào để hoàn tất."));
            }
            else if (states[lane.Id] == LaneState.Harvested)
            {
                already.Add(number);
            }
            else
            {
                toFinish.Add((lane, number, latest));
            }
        }

        if (problems.Count > 0)
        {
            return AppError.Conflict(
                UserMessages.WrongStatus($"Raft {raft.Code}", "not ready: some lanes cannot be finished"),
                new Dictionary<string, object?> { ["lanes"] = problems });
        }

        var after = new Dictionary<Guid, LaneState>(states);
        foreach (var (lane, number, latest) in toFinish)
        {
            // Tracked copy of the lot, to change its lane row (the same instance when two lanes share a lot).
            var lot = await lots.GetForEditAsync(latest.LotId, cancellationToken) ?? throw new InvalidOperationException("The lot of a lane row must exist.");
            lot.Lanes.Single(l => l.LaneId == lane.Id).IsFinished = true;
            unitOfWork.MarkChanged(lot);
            audit.Record(HarvestLotAudit.Entry(HarvestLaneFinishService.LaneFinished, lot, raft.ZoneId) with
            {
                Before = new { Lane = number, IsFinished = false },
                After = new { Lane = number, IsFinished = true },
            });
            after[lane.Id] = LaneState.Harvested;
        }

        var finished = toFinish.Select(t => t.Number).ToList();
        var every = HarvestLaneFinishService.EveryHarvested(raft, after);
        if (finished.Count > 0)
        {
            unitOfWork.MarkChanged(cycle);
            audit.Record(new AuditEntry(LanesFinished, nameof(Raft))
            {
                RecordId = raft.Id, RecordCode = raft.Code, ZoneId = raft.ZoneId,
                Before = new { FinishedLanes = Array.Empty<int>() },
                After = new { FinishedLanes = finished },
            });
            if (every && cycle.CloseRemindedAt is null)
            {
                var actor = currentUser.AccountId ?? throw new InvalidOperationException("A lane finish needs an authenticated caller.");
                cycle.CloseRemindedAt = timeProvider.GetUtcNow();
                await notifier.CloseReminderAsync(cycle, raft, actor, cancellationToken);
            }

            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        var saved = UserMessages.Saved("Lane harvest");
        return new LanesHarvestFinished(finished, already, every, RowVersionGuard.Encode(cycle.RowVersion), saved.Text, saved.Code);
    }
}
