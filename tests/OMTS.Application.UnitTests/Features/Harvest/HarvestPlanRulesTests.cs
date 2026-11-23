using OMTS.Application.Features.Harvest.Plans;
using OMTS.Domain.Constants;
using static OMTS.Application.Features.Harvest.Plans.HarvestPlanAction;

namespace OMTS.Application.UnitTests.Features.Harvest;

/// <summary>The rules of a harvest plan: the state of a lane and a line, the status moves after a lot or a lane left out, the buttons of each caller, and the lanes two open plans both want.</summary>
public class HarvestPlanRulesTests
{
    private const int Draft = StatusIds.HarvestPlan.Draft;
    private const int Assigned = StatusIds.HarvestPlan.Assigned;
    private const int Received = StatusIds.HarvestPlan.Received;
    private const int HandedOver = StatusIds.HarvestPlan.HandedOver;
    private const int Harvesting = StatusIds.HarvestPlan.Harvesting;
    private const int Completed = StatusIds.HarvestPlan.Completed;
    private const int Cancelled = StatusIds.HarvestPlan.Cancelled;

    private static readonly HarvestPlanCaller Owner = new(IsOwner: true, CanEdit: true, CanDelete: true);
    private static readonly HarvestPlanCaller Manager = new(IsOwner: false, CanEdit: false, CanDelete: false);

    private static IReadOnlyList<HarvestPlanAction> Buttons(int status, HarvestPlanCaller caller, bool hasLot = false, bool canHarvestNow = false, bool mayHarvestInPlan = false, bool allLinesDone = false) =>
        HarvestPlanRules.AllowedActions(status, caller, hasLot, canHarvestNow, mayHarvestInPlan, allLinesDone);

    private static PlanLineFacts Open(int lanes = 2) => new(false, lanes, 0);
    private static PlanLineFacts WithLot(int lanes = 2) => new(true, lanes, 0);
    private static PlanLineFacts AllSkipped(int lanes = 2) => new(false, lanes, lanes);
    private static PlanLineFacts SomeSkipped(int lanes = 2) => new(false, lanes, 1);

    // ---- Lanes and lines ----

    [Theory]
    [InlineData(false, false, HarvestPlanLaneState.Planned)]
    [InlineData(true, false, HarvestPlanLaneState.Harvested)]
    [InlineData(false, true, HarvestPlanLaneState.Skipped)]
    public void A_lane_is_harvested_when_it_is_in_the_lot_and_skipped_when_it_is_left_out(bool inLot, bool skipped, HarvestPlanLaneState expected) =>
        Assert.Equal(expected, HarvestPlanRules.LaneState(inLot, skipped));

    [Fact]
    public void A_line_is_done_with_its_lot_or_when_every_lane_is_left_out()
    {
        Assert.False(HarvestPlanRules.LineDone(Open()));
        Assert.False(HarvestPlanRules.LineDone(SomeSkipped()));
        Assert.True(HarvestPlanRules.LineDone(WithLot()));
        Assert.True(HarvestPlanRules.LineDone(AllSkipped()));
        Assert.Equal(HarvestPlanLineState.Open, HarvestPlanRules.LineState(SomeSkipped()));
        Assert.Equal(HarvestPlanLineState.Harvested, HarvestPlanRules.LineState(WithLot()));
        Assert.Equal(HarvestPlanLineState.Skipped, HarvestPlanRules.LineState(AllSkipped()));
    }

    [Fact]
    public void A_line_with_no_lanes_is_never_done_by_leaving_lanes_out()
    {
        Assert.False(HarvestPlanRules.LineDone(new PlanLineFacts(false, 0, 0)));
    }

    [Fact]
    public void A_line_is_locked_once_it_has_a_lot_or_a_lane_left_out()
    {
        Assert.False(HarvestPlanRules.LineLocked(Open()));
        Assert.True(HarvestPlanRules.LineLocked(WithLot()));
        Assert.True(HarvestPlanRules.LineLocked(SomeSkipped()));
    }

    // ---- Status moves ----

    [Fact]
    public void The_first_lot_of_a_received_plan_starts_the_harvest()
    {
        Assert.Equal([Harvesting], HarvestPlanRules.Settle(Received, [WithLot(), Open()], allDoneBefore: false));
    }

    [Fact]
    public void The_first_lane_left_out_starts_the_harvest()
    {
        Assert.Equal([Harvesting], HarvestPlanRules.Settle(Received, [SomeSkipped(), Open()], allDoneBefore: false));
    }

    [Fact]
    public void A_plan_with_nothing_recorded_stays_received()
    {
        Assert.Empty(HarvestPlanRules.Settle(Received, [Open(), Open()], allDoneBefore: false));
    }

    [Fact]
    public void An_assigned_plan_never_moves_with_the_lines_nobody_received_it()
    {
        Assert.Empty(HarvestPlanRules.Settle(Assigned, [WithLot(), Open()], allDoneBefore: false));
        Assert.Empty(HarvestPlanRules.Settle(Assigned, [AllSkipped()], allDoneBefore: false));
    }

    [Fact]
    public void The_system_never_completes_a_plan_whatever_the_lines()
    {
        Assert.Empty(HarvestPlanRules.Settle(Harvesting, [WithLot(), WithLot()], allDoneBefore: false));
        Assert.Empty(HarvestPlanRules.Settle(Harvesting, [WithLot(), AllSkipped()], allDoneBefore: true));
        Assert.Equal([Harvesting], HarvestPlanRules.Settle(Received, [WithLot()], allDoneBefore: false));
        Assert.Equal([Harvesting], HarvestPlanRules.Settle(Received, [AllSkipped(), AllSkipped(3)], allDoneBefore: false));
    }

    [Fact]
    public void A_handed_over_plan_opens_again_when_a_line_is_open_again_and_stays_handed_over_otherwise()
    {
        Assert.Equal([Harvesting], HarvestPlanRules.Settle(HandedOver, [Open(), WithLot()], allDoneBefore: true));
        Assert.Empty(HarvestPlanRules.Settle(HandedOver, [WithLot(), AllSkipped()], allDoneBefore: true));
    }

    [Fact]
    public void A_harvesting_plan_with_a_line_still_open_stays_harvesting()
    {
        Assert.Empty(HarvestPlanRules.Settle(Harvesting, [WithLot(), Open()], allDoneBefore: false));
    }

    [Fact]
    public void A_completed_plan_that_was_whole_opens_again_when_a_line_is_open_again()
    {
        Assert.Equal([Harvesting], HarvestPlanRules.Settle(Completed, [Open(), WithLot()], allDoneBefore: true));
    }

    [Fact]
    public void A_plan_the_owner_finished_with_lanes_still_to_do_stays_completed()
    {
        Assert.Empty(HarvestPlanRules.Settle(Completed, [Open(), WithLot()], allDoneBefore: false));
        Assert.Empty(HarvestPlanRules.Settle(Completed, [WithLot(), WithLot()], allDoneBefore: true));
    }

    [Theory]
    [InlineData(Draft)]
    [InlineData(Cancelled)]
    public void A_draft_and_a_cancelled_plan_never_move_with_the_lines(int status)
    {
        Assert.Empty(HarvestPlanRules.Settle(status, [WithLot()], allDoneBefore: false));
    }

    [Fact]
    public void A_plan_with_no_lines_never_completes()
    {
        Assert.Empty(HarvestPlanRules.Settle(Harvesting, [], allDoneBefore: false));
        Assert.False(HarvestPlanRules.AllDone([]));
    }

    // ---- Status guards ----

    [Fact]
    public void Each_command_allows_its_statuses()
    {
        Assert.True(HarvestPlanRules.AllowsAssign(Draft));
        Assert.False(HarvestPlanRules.AllowsAssign(Assigned));
        Assert.True(HarvestPlanRules.AllowsReceive(Assigned));
        Assert.False(HarvestPlanRules.AllowsReceive(Draft));
        Assert.False(HarvestPlanRules.AllowsReceive(Received));
        Assert.True(HarvestPlanRules.AllowsHandOver(Harvesting));
        Assert.False(HarvestPlanRules.AllowsHandOver(Received));
        Assert.False(HarvestPlanRules.AllowsHandOver(HandedOver));
        Assert.True(HarvestPlanRules.AllowsFinish(HandedOver));
        Assert.False(HarvestPlanRules.AllowsFinish(Harvesting));
        Assert.False(HarvestPlanRules.AllowsFinish(Assigned));
        Assert.True(HarvestPlanRules.AllowsCancel(Draft));
        Assert.True(HarvestPlanRules.AllowsCancel(Assigned));
        Assert.True(HarvestPlanRules.AllowsCancel(Received));
        Assert.False(HarvestPlanRules.AllowsCancel(Harvesting));
        Assert.False(HarvestPlanRules.AllowsCancel(HandedOver));
        Assert.True(HarvestPlanRules.AllowsHarvest(Received));
        Assert.True(HarvestPlanRules.AllowsHarvest(Harvesting));
        Assert.False(HarvestPlanRules.AllowsHarvest(Assigned));
        Assert.False(HarvestPlanRules.AllowsHarvest(HandedOver));
        Assert.False(HarvestPlanRules.AllowsHarvest(Draft));
        Assert.False(HarvestPlanRules.AllowsHarvest(Completed));
        Assert.True(HarvestPlanRules.AllowsUnskip(Completed));
        Assert.True(HarvestPlanRules.AllowsUnskip(HandedOver));
        Assert.False(HarvestPlanRules.AllowsUnskip(Assigned));
        Assert.False(HarvestPlanRules.AllowsUnskip(Cancelled));
        Assert.True(HarvestPlanRules.AllowsEdit(Harvesting));
        Assert.True(HarvestPlanRules.AllowsEdit(Received));
        Assert.False(HarvestPlanRules.AllowsEdit(HandedOver));
        Assert.False(HarvestPlanRules.AllowsEdit(Completed));
    }

    // ---- Buttons ----

    [Fact]
    public void The_owner_edits_and_hands_over_a_draft_and_may_cancel_it()
    {
        Assert.Equal([Edit, Assign, Cancel], HarvestPlanRules.AllowedActions(Draft, Owner, hasLot: false, canHarvestNow: true));
    }

    [Fact]
    public void An_assigned_plan_offers_the_receive_to_a_manager_and_the_edit_to_the_owner_but_no_harvest()
    {
        Assert.Equal([Edit, HarvestPlanAction.Receive, Cancel], Buttons(Assigned, Owner, canHarvestNow: true, mayHarvestInPlan: true));
        Assert.Equal([HarvestPlanAction.Receive], Buttons(Assigned, Manager, canHarvestNow: true, mayHarvestInPlan: true));
        // A reader (a caller that may not harvest in a zone of the plan) gets no button.
        Assert.Empty(Buttons(Assigned, Manager));
    }

    [Fact]
    public void A_received_plan_offers_the_harvest_and_the_cancel_while_no_lot_exists()
    {
        Assert.Equal([HarvestPlanAction.Harvest, SkipLane], Buttons(Received, Manager, canHarvestNow: true, mayHarvestInPlan: true));
        Assert.Equal([Edit, Cancel, HarvestPlanAction.Harvest, SkipLane], Buttons(Received, Owner, canHarvestNow: true, mayHarvestInPlan: true));
        Assert.Empty(Buttons(Received, Manager));
    }

    [Fact]
    public void The_hand_over_needs_every_line_of_the_whole_plan_done()
    {
        var open = Buttons(Harvesting, Manager, hasLot: true, canHarvestNow: true, mayHarvestInPlan: true, allLinesDone: false);
        var done = Buttons(Harvesting, Manager, hasLot: true, canHarvestNow: false, mayHarvestInPlan: true, allLinesDone: true);

        Assert.Equal([HarvestPlanAction.Harvest, SkipLane], open);
        Assert.Equal([HarvestPlanAction.HandOver], done);
        // The Owner edits and finishes only after the hand-over, and a reader never hands over.
        Assert.Equal([Edit, HarvestPlanAction.HandOver], Buttons(Harvesting, Owner, hasLot: true, mayHarvestInPlan: true, allLinesDone: true));
        Assert.Equal([Edit], Buttons(Harvesting, Owner, hasLot: true, allLinesDone: true));
    }

    [Fact]
    public void A_plan_being_harvested_is_handed_over_not_cancelled()
    {
        var actions = Buttons(Harvesting, Owner, hasLot: true, canHarvestNow: true, mayHarvestInPlan: true);

        Assert.Equal([Edit, HarvestPlanAction.Harvest, SkipLane], actions);
        Assert.DoesNotContain(Cancel, actions);
        Assert.DoesNotContain(Finish, actions);
    }

    [Fact]
    public void A_handed_over_plan_is_completed_by_the_owner_alone()
    {
        Assert.Equal([Finish], Buttons(HandedOver, Owner, hasLot: true, mayHarvestInPlan: true, allLinesDone: true));
        // A caller with the right to edit plans in every zone is still not the Owner: only the Owner completes.
        Assert.Empty(Buttons(HandedOver, new HarvestPlanCaller(IsOwner: false, CanEdit: true, CanDelete: true), hasLot: true, mayHarvestInPlan: true, allLinesDone: true));
        Assert.Empty(Buttons(HandedOver, Manager, hasLot: true, mayHarvestInPlan: true, allLinesDone: true));
    }

    [Fact]
    public void A_plan_that_is_over_offers_nothing()
    {
        Assert.Empty(Buttons(Completed, Owner, hasLot: true));
        Assert.Empty(Buttons(Cancelled, Owner, canHarvestNow: true, mayHarvestInPlan: true));
    }

    // ---- Clashes ----

    [Fact]
    public void A_lane_wanted_by_two_open_plans_is_a_clash_for_its_raft_and_the_other_plan()
    {
        var (raft, other) = (Guid.NewGuid(), Guid.NewGuid());
        var (lane1, lane2, lane3) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var wanted = new Dictionary<Guid, IReadOnlySet<Guid>> { [raft] = new HashSet<Guid> { lane1, lane2 } };

        var clashes = HarvestPlanRules.Clashes([(raft, lane2, "KHT000002"), (raft, lane3, "KHT000002"), (other, lane1, "KHT000003")], wanted);

        var clash = Assert.Single(clashes);
        Assert.Equal(raft, clash.RaftId);
        Assert.Equal([lane2], clash.LaneIds);
        Assert.Equal("KHT000002", clash.OtherPlanCode);
    }

    [Fact]
    public void Lanes_nobody_else_waits_on_are_no_clash()
    {
        var raft = Guid.NewGuid();
        var wanted = new Dictionary<Guid, IReadOnlySet<Guid>> { [raft] = new HashSet<Guid> { Guid.NewGuid() } };

        Assert.Empty(HarvestPlanRules.Clashes([(raft, Guid.NewGuid(), "KHT000002")], wanted));
    }
}
