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

    /// <summary>Draft, Assigned, Received, Harvesting or HandedOver: the plan is not over.</summary>
    public static bool IsOpen(int statusId) =>
        statusId is StatusIds.HarvestPlan.Draft or StatusIds.HarvestPlan.Assigned or StatusIds.HarvestPlan.Received or StatusIds.HarvestPlan.Harvesting or StatusIds.HarvestPlan.HandedOver;

    /// <summary>The Owner changes a plan until the Zone Manager hands it over.</summary>
    public static bool AllowsEdit(int statusId) => IsOpen(statusId) && statusId != StatusIds.HarvestPlan.HandedOver;

    public static bool AllowsAssign(int statusId) => statusId == StatusIds.HarvestPlan.Draft;

    /// <summary>"Nhận": a Zone Manager receives a plan the Owner assigned; there is no refusal.</summary>
    public static bool AllowsReceive(int statusId) => statusId == StatusIds.HarvestPlan.Assigned;

    /// <summary>"Bàn giao": a Zone Manager hands over a plan being harvested once every line is done (see <see cref="AllDone"/>).</summary>
    public static bool AllowsHandOver(int statusId) => statusId == StatusIds.HarvestPlan.Harvesting;

    /// <summary>"Hoàn thành" (the Finish action): only the Owner, only a plan the Zone Manager handed over. The system never completes a plan by itself.</summary>
    public static bool AllowsFinish(int statusId) => statusId == StatusIds.HarvestPlan.HandedOver;

    /// <summary>A plan with no lot yet can be cancelled while it is a Draft, Assigned or Received; once harvesting starts it is handed over instead.</summary>
    public static bool AllowsCancel(int statusId) => statusId is StatusIds.HarvestPlan.Draft or StatusIds.HarvestPlan.Assigned or StatusIds.HarvestPlan.Received;

    /// <summary>Lots are recorded from the lines, and lanes left out, only after a Zone Manager received the plan and before it is handed over.</summary>
    public static bool AllowsHarvest(int statusId) => statusId is StatusIds.HarvestPlan.Received or StatusIds.HarvestPlan.Harvesting;

    /// <summary>A lane left out is put back while the plan is being harvested; a plan already handed over or completed opens again (Harvesting).</summary>
    public static bool AllowsUnskip(int statusId) =>
        statusId is StatusIds.HarvestPlan.Received or StatusIds.HarvestPlan.Harvesting or StatusIds.HarvestPlan.HandedOver or StatusIds.HarvestPlan.Completed;

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
    /// The statuses a plan passes through after a lot, a lane left out or a change of its lines, in order (none when nothing moves). A Received plan becomes Harvesting with its first lot or lane left out and stays
    /// there until a Zone Manager hands it over: the system never completes a plan. A HandedOver plan, or a Completed one that was whole before the change, opens again (Harvesting) when a line is not done any more.
    /// </summary>
    /// <param name="allDoneBefore">Every line was done before this change; tells a Completed plan that was whole from one closed with lanes still to do.</param>
    public static IReadOnlyList<int> Settle(int statusId, IReadOnlyCollection<PlanLineFacts> lines, bool allDoneBefore)
    {
        var moves = new List<int>();
        var done = AllDone(lines);
        switch (statusId)
        {
            case StatusIds.HarvestPlan.Received:
                if (Started(lines))
                {
                    moves.Add(StatusIds.HarvestPlan.Harvesting);
                }

                break;
            case StatusIds.HarvestPlan.HandedOver:
                if (!done)
                {
                    moves.Add(StatusIds.HarvestPlan.Harvesting);
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
    /// The buttons of a plan for a caller. Edit and Assign are the Owner's (HARVEST_PLANS Edit), Finish (the Owner completes a handed over plan) is the Owner's alone, Cancel needs Delete and a plan without a lot;
    /// Receive, HandOver, Harvest and SkipLane need the right to harvest (HARVESTS Create) in a zone of the plan the caller sees, whether the caller is the Owner or a Zone Manager.
    /// </summary>
    /// <param name="canHarvestNow">Some line the caller sees is still open and in a zone where the caller may harvest.</param>
    /// <param name="mayHarvestInPlan">The caller sees a line of the plan in a zone where it may harvest.</param>
    /// <param name="allLinesDone">Every line of the whole plan, also in zones the caller does not see, has its lot or has every lane left out.</param>
    public static IReadOnlyList<HarvestPlanAction> AllowedActions(int statusId, HarvestPlanCaller caller, bool hasLot, bool canHarvestNow, bool mayHarvestInPlan = false, bool allLinesDone = false)
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

        if (AllowsReceive(statusId) && mayHarvestInPlan)
        {
            actions.Add(HarvestPlanAction.Receive);
        }

        if (AllowsHandOver(statusId) && mayHarvestInPlan && allLinesDone)
        {
            actions.Add(HarvestPlanAction.HandOver);
        }

        if (AllowsFinish(statusId) && caller.IsOwner)
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
