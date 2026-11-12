using OMTS.Application.Features.Harvest.Plans;
using OMTS.Domain.Constants;
using static OMTS.Application.Features.Harvest.Plans.HarvestPlanAction;

namespace OMTS.Application.UnitTests.Features.Harvest;

/// <summary>The rules of a harvest plan: the state of a lane and a line, the status moves after a lot or a lane left out, the buttons of each caller, and the lanes two open plans both want.</summary>
public class HarvestPlanRulesTests
{
    private const int Draft = StatusIds.HarvestPlan.Draft;
    private const int Assigned = StatusIds.HarvestPlan.Assigned;
    private const int Harvesting = StatusIds.HarvestPlan.Harvesting;
    private const int Completed = StatusIds.HarvestPlan.Completed;
    private const int Cancelled = StatusIds.HarvestPlan.Cancelled;

    private static readonly HarvestPlanCaller Owner = new(IsOwner: true, CanEdit: true, CanDelete: true);
    private static readonly HarvestPlanCaller Manager = new(IsOwner: false, CanEdit: false, CanDelete: false);

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
    public void The_first_lot_of_an_assigned_plan_starts_the_harvest()
    {
        Assert.Equal([Harvesting], HarvestPlanRules.Settle(Assigned, [WithLot(), Open()], allDoneBefore: false));
    }

    [Fact]
    public void The_first_lane_left_out_starts_the_harvest()
    {
        Assert.Equal([Harvesting], HarvestPlanRules.Settle(Assigned, [SomeSkipped(), Open()], allDoneBefore: false));
    }

    [Fact]
    public void A_plan_with_nothing_recorded_stays_assigned()
    {
        Assert.Empty(HarvestPlanRules.Settle(Assigned, [Open(), Open()], allDoneBefore: false));
    }

    [Fact]
    public void The_last_lot_of_a_harvesting_plan_completes_it()
    {
        Assert.Equal([Completed], HarvestPlanRules.Settle(Harvesting, [WithLot(), WithLot()], allDoneBefore: false));
        Assert.Equal([Completed], HarvestPlanRules.Settle(Harvesting, [WithLot(), AllSkipped()], allDoneBefore: false));
    }

    [Fact]
    public void A_plan_of_one_line_goes_from_assigned_through_harvesting_to_completed_with_its_lot()
    {
        Assert.Equal([Harvesting, Completed], HarvestPlanRules.Settle(Assigned, [WithLot()], allDoneBefore: false));
    }

    [Fact]
    public void An_assigned_plan_with_every_lane_left_out_and_no_lot_goes_straight_to_completed()
    {
        Assert.Equal([Completed], HarvestPlanRules.Settle(Assigned, [AllSkipped(), AllSkipped(3)], allDoneBefore: false));
    }

    [Fact]
    public void A_harvesting_plan_with_a_line_still_open_stays_harvesting()
    {
        Assert.Empty(HarvestPlanRules.Settle(Harvesting, [WithLot(), Open()], allDoneBefore: false));
    }

    [Fact]
    public void A_plan_that_finished_by_itself_opens_again_when_a_line_is_open_again()
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
        Assert.True(HarvestPlanRules.AllowsFinish(Harvesting));
        Assert.False(HarvestPlanRules.AllowsFinish(Assigned));
        Assert.True(HarvestPlanRules.AllowsCancel(Draft));
        Assert.True(HarvestPlanRules.AllowsCancel(Assigned));
        Assert.False(HarvestPlanRules.AllowsCancel(Harvesting));
        Assert.True(HarvestPlanRules.AllowsHarvest(Assigned));
        Assert.True(HarvestPlanRules.AllowsHarvest(Harvesting));
        Assert.False(HarvestPlanRules.AllowsHarvest(Draft));
        Assert.False(HarvestPlanRules.AllowsHarvest(Completed));
        Assert.True(HarvestPlanRules.AllowsUnskip(Completed));
        Assert.False(HarvestPlanRules.AllowsUnskip(Cancelled));
        Assert.True(HarvestPlanRules.AllowsEdit(Harvesting));
        Assert.False(HarvestPlanRules.AllowsEdit(Completed));
    }

    // ---- Buttons ----

    [Fact]
    public void The_owner_edits_and_hands_over_a_draft_and_may_cancel_it()
    {
        Assert.Equal([Edit, Assign, Cancel], HarvestPlanRules.AllowedActions(Draft, Owner, hasLot: false, canHarvestNow: true));
    }

    [Fact]
    public void A_handed_over_plan_offers_the_harvest_to_a_manager_and_the_edit_to_the_owner()
    {
        Assert.Equal([Edit, Cancel, HarvestPlanAction.Harvest, SkipLane], HarvestPlanRules.AllowedActions(Assigned, Owner, hasLot: false, canHarvestNow: true));
        Assert.Equal([HarvestPlanAction.Harvest, SkipLane], HarvestPlanRules.AllowedActions(Assigned, Manager, hasLot: false, canHarvestNow: true));
        // A reader (or a caller with no line open in a zone it harvests in) gets no harvest button.
        Assert.Empty(HarvestPlanRules.AllowedActions(Assigned, Manager, hasLot: false, canHarvestNow: false));
    }

    [Fact]
    public void A_plan_being_harvested_is_finished_not_cancelled()
    {
        var actions = HarvestPlanRules.AllowedActions(Harvesting, Owner, hasLot: true, canHarvestNow: true);

        Assert.Equal([Edit, Finish, HarvestPlanAction.Harvest, SkipLane], actions);
        Assert.DoesNotContain(Cancel, actions);
    }

    [Fact]
    public void With_no_line_open_nothing_is_left_to_harvest()
    {
        Assert.Equal([Edit, Finish], HarvestPlanRules.AllowedActions(Harvesting, Owner, hasLot: true, canHarvestNow: false));
    }

    [Fact]
    public void A_plan_that_is_over_offers_nothing()
    {
        Assert.Empty(HarvestPlanRules.AllowedActions(Completed, Owner, hasLot: true, canHarvestNow: false));
        Assert.Empty(HarvestPlanRules.AllowedActions(Cancelled, Owner, hasLot: false, canHarvestNow: true));
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
