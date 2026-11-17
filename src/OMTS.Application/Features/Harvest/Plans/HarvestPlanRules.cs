using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Harvest;

namespace OMTS.Application.Features.Harvest.Plans;

/// <summary>What the rules need to know of a line: whether its lot is recorded, how many lanes it plans and how many of them are left out.</summary>
public sealed record PlanLineFacts(bool HasLot, int LaneCount, int SkippedCount);

/// <summary>What the caller may do with a plan, which has the zones of its lines: the Owner everything; a Zone Manager only harvests (HARVESTS Create in a zone of a line it sees). Edit and Delete need the right in every zone of the plan.</summary>
public sealed record HarvestPlanCaller(bool IsOwner, bool CanEdit, bool CanDelete);

/// <summary>A lane of one raft that two open plans both want.</summary>
public sealed record LaneClash(Guid RaftId, IReadOnlyList<Guid> LaneIds, string OtherPlanCode);

/// <summary>
/// The rules of a harvest plan as pure functions, so they are unit tested and every screen gives one answer: the state of a lane and a line, the moves of the status that follow a lot or a lane left out,
/// and the buttons of a caller.
/// </summary>
public static class HarvestPlanRules
{
    public const int MaxLines = 50;
    public const int MaxNoteLength = 1000;
    public const int MaxLineNoteLength = 500;
    public const int MaxSkipReasonLength = 500;
    public const int MaxOrders = 20;

    /// <summary>Draft, Assigned or Harvesting: the plan is not over.</summary>
    public static bool IsOpen(int statusId) => statusId is StatusIds.HarvestPlan.Draft or StatusIds.HarvestPlan.Assigned or StatusIds.HarvestPlan.Harvesting;

    public static bool AllowsEdit(int statusId) => IsOpen(statusId);

    public static bool AllowsAssign(int statusId) => statusId == StatusIds.HarvestPlan.Draft;

    /// <summary>"Kết thúc": only a plan being harvested is finished by hand; the lanes still to do stay as they are.</summary>
    public static bool AllowsFinish(int statusId) => statusId == StatusIds.HarvestPlan.Harvesting;

    /// <summary>A plan that was handed over and has no lot yet can still be cancelled; once harvesting starts it is finished instead.</summary>
    public static bool AllowsCancel(int statusId) => statusId is StatusIds.HarvestPlan.Draft or StatusIds.HarvestPlan.Assigned;

    /// <summary>Lots are recorded from the lines, and lanes left out, only while the plan is with the Zone Managers.</summary>
    public static bool AllowsHarvest(int statusId) => statusId is StatusIds.HarvestPlan.Assigned or StatusIds.HarvestPlan.Harvesting;

    /// <summary>A lane left out is put back while the plan is with the Zone Managers, or after it finished because of the lanes left out.</summary>
    public static bool AllowsUnskip(int statusId) => statusId is StatusIds.HarvestPlan.Assigned or StatusIds.HarvestPlan.Harvesting or StatusIds.HarvestPlan.Completed;

    public static HarvestPlanLaneState LaneState(bool inLot, bool skipped) =>
        inLot ? HarvestPlanLaneState.Harvested : skipped ? HarvestPlanLaneState.Skipped : HarvestPlanLaneState.Planned;

    /// <summary>The facts of a loaded line: its lot, its lanes and the lanes left out.</summary>
    public static PlanLineFacts FactsOf(HarvestPlanLine line) => new(line.Lot is not null, line.Lanes.Count, line.Lanes.Count(l => l.IsSkipped));

    /// <summary>A line is done with its lot, or when it has lanes and every one of them is left out.</summary>
    public static bool LineDone(PlanLineFacts line) => line.HasLot || (line.LaneCount > 0 && line.SkippedCount >= line.LaneCount);

    public static HarvestPlanLineState LineState(PlanLineFacts line) =>
        line.HasLot ? HarvestPlanLineState.Harvested : LineDone(line) ? HarvestPlanLineState.Skipped : HarvestPlanLineState.Open;

    /// <summary>A line with its lot or a lane left out can no longer be changed by the Owner.</summary>
    public static bool LineLocked(PlanLineFacts line) => line.HasLot || line.SkippedCount > 0;

    public static bool AllDone(IReadOnlyCollection<PlanLineFacts> lines) => lines.Count > 0 && lines.All(LineDone);

    /// <summary>The first lot or the first lane left out starts the harvest.</summary>
    public static bool Started(IReadOnlyCollection<PlanLineFacts> lines) => lines.Any(l => l.HasLot || l.SkippedCount > 0);

    /// <summary>
    /// The statuses a plan passes through after a lot, a lane left out or a change of its lines, in order (none when nothing moves). An Assigned plan whose every lane is left out and has no lot
    /// goes straight to Completed; otherwise it is Harvesting first. A Completed plan opens again (Harvesting) when it had finished by itself, every line was done, and one is not any more.
    /// </summary>
    /// <param name="allDoneBefore">Every line was done before this change; what tells a plan that finished by itself from one the Owner finished by hand with lanes still to do.</param>
    public static IReadOnlyList<int> Settle(int statusId, IReadOnlyCollection<PlanLineFacts> lines, bool allDoneBefore)
    {
        var moves = new List<int>();
        var done = AllDone(lines);
        switch (statusId)
        {
            case StatusIds.HarvestPlan.Assigned:
                if (done && !lines.Any(l => l.HasLot))
                {
                    moves.Add(StatusIds.HarvestPlan.Completed);
                    break;
                }

                if (Started(lines))
                {
                    moves.Add(StatusIds.HarvestPlan.Harvesting);
                    if (done)
                    {
                        moves.Add(StatusIds.HarvestPlan.Completed);
                    }
                }

                break;
            case StatusIds.HarvestPlan.Harvesting:
                if (done)
                {
                    moves.Add(StatusIds.HarvestPlan.Completed);
                }

                break;
            case StatusIds.HarvestPlan.Completed:
                if (!done && allDoneBefore)
                {
                    moves.Add(StatusIds.HarvestPlan.Harvesting);
                }

                break;
        }

        return moves;
    }

    /// <summary>
    /// The buttons of a plan for a caller. Edit, Assign and Finish are the Owner's (HARVEST_PLANS Edit), Cancel needs Delete and a plan without a lot; Harvest and SkipLane need a line still open in a zone the
    /// caller harvests in (HARVESTS Create), whether the caller is the Owner or a Zone Manager.
    /// </summary>
    /// <param name="canHarvestNow">Some line the caller sees is still open and in a zone where the caller may harvest.</param>
    public static IReadOnlyList<HarvestPlanAction> AllowedActions(int statusId, HarvestPlanCaller caller, bool hasLot, bool canHarvestNow)
    {
        var actions = new List<HarvestPlanAction>();
        var mayEdit = caller.IsOwner || caller.CanEdit;
        if (AllowsEdit(statusId) && mayEdit)
        {
            actions.Add(HarvestPlanAction.Edit);
        }

        if (AllowsAssign(statusId) && mayEdit)
        {
            actions.Add(HarvestPlanAction.Assign);
        }

        if (AllowsFinish(statusId) && mayEdit)
        {
            actions.Add(HarvestPlanAction.Finish);
        }

        if (AllowsCancel(statusId) && !hasLot && (caller.IsOwner || caller.CanDelete))
        {
            actions.Add(HarvestPlanAction.Cancel);
        }

        if (AllowsHarvest(statusId) && canHarvestNow)
        {
            actions.Add(HarvestPlanAction.Harvest);
            actions.Add(HarvestPlanAction.SkipLane);
        }

        return actions;
    }

    /// <summary>
    /// The lanes the plan wants that another open plan already waits on, one clash per raft and other plan. <paramref name="claims"/> are the lanes of the other plans; <paramref name="wanted"/> the lanes of this one by raft.
    /// </summary>
    public static IReadOnlyList<LaneClash> Clashes(IEnumerable<(Guid RaftId, Guid LaneId, string PlanCode)> claims, IReadOnlyDictionary<Guid, IReadOnlySet<Guid>> wanted) =>
        claims
            .Where(c => wanted.TryGetValue(c.RaftId, out var lanes) && lanes.Contains(c.LaneId))
            .GroupBy(c => (c.RaftId, c.PlanCode))
            .Select(g => new LaneClash(g.Key.RaftId, g.Select(c => c.LaneId).Distinct().ToList(), g.Key.PlanCode))
            .ToList();
}
