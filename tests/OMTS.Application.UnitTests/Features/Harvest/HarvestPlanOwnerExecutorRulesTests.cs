using OMTS.Application.Features.Harvest.Plans;
using OMTS.Domain.Constants;
using OMTS.Domain.Enums;
using static OMTS.Application.Features.Harvest.Plans.HarvestPlanAction;

namespace OMTS.Application.UnitTests.Features.Harvest;

/// <summary>
/// Owner decision of 25/11/2026: the Owner picks, when assigning a plan, who harvests it. A plan the Owner harvests himself is Assigned, Harvesting, then Completed by him: no reception, no hand-over.
/// The plan of Zone Managers keeps its flow (Assigned, Received, Harvesting, HandedOver, Completed).
/// </summary>
public class HarvestPlanOwnerExecutorRulesTests
{
    private const int Draft = StatusIds.HarvestPlan.Draft;
    private const int Assigned = StatusIds.HarvestPlan.Assigned;
    private const int Received = StatusIds.HarvestPlan.Received;
    private const int HandedOver = StatusIds.HarvestPlan.HandedOver;
    private const int Harvesting = StatusIds.HarvestPlan.Harvesting;
    private const int Completed = StatusIds.HarvestPlan.Completed;
    private const HarvestPlanExecutor ByOwner = HarvestPlanExecutor.Owner;
    private const HarvestPlanExecutor ByManager = HarvestPlanExecutor.ZoneManager;

    private static readonly HarvestPlanCaller Owner = new(IsOwner: true, CanEdit: true, CanDelete: true);

    private static PlanLineFacts Open() => new(false, 2, 0);
    private static PlanLineFacts Done() => new(true, 2, 0);

    [Fact]
    public void The_default_executor_is_the_zone_manager_flow_unchanged()
    {
        Assert.Equal(HarvestPlanExecutor.ZoneManager, new Domain.Entities.Harvest.HarvestPlan().Executor);
        Assert.True(HarvestPlanRules.AllowsReceive(Assigned));
        Assert.True(HarvestPlanRules.AllowsHandOver(Harvesting));
        Assert.True(HarvestPlanRules.AllowsFinish(HandedOver));
        Assert.False(HarvestPlanRules.AllowsHarvest(Assigned));
        Assert.True(HarvestPlanRules.AllowsHarvest(Received));
    }

    [Theory]
    [InlineData(Draft, false)]
    [InlineData(Assigned, true)]
    [InlineData(Harvesting, true)]
    [InlineData(Received, false)]
    [InlineData(HandedOver, false)]
    [InlineData(Completed, false)]
    public void The_owner_harvests_and_leaves_lanes_out_right_after_assigned_without_a_reception(int status, bool allowed)
    {
        Assert.Equal(allowed, HarvestPlanRules.AllowsHarvest(status, ByOwner));
        Assert.False(HarvestPlanRules.AllowsReceive(status, ByOwner));
        Assert.False(HarvestPlanRules.AllowsHandOver(status, ByOwner));
    }

    [Fact]
    public void The_owner_finishes_from_harvesting_only_when_every_line_is_done()
    {
        Assert.True(HarvestPlanRules.AllowsFinish(Harvesting, ByOwner, allLinesDone: true));
        Assert.False(HarvestPlanRules.AllowsFinish(Harvesting, ByOwner, allLinesDone: false));
        Assert.False(HarvestPlanRules.AllowsFinish(Assigned, ByOwner, allLinesDone: true));
        Assert.False(HarvestPlanRules.AllowsFinish(HandedOver, ByOwner, allLinesDone: true));
        // The plan of Zone Managers is finished after the hand-over, whatever the lines.
        Assert.False(HarvestPlanRules.AllowsFinish(Harvesting, ByManager, allLinesDone: true));
    }

    [Fact]
    public void The_first_lot_or_lane_left_out_moves_an_assigned_owner_plan_to_harvesting_and_a_manager_plan_stays_assigned()
    {
        Assert.Equal([Harvesting], HarvestPlanRules.Settle(Assigned, [Done(), Open()], allDoneBefore: false, ByOwner));
        Assert.Equal([Harvesting], HarvestPlanRules.Settle(Assigned, [new PlanLineFacts(false, 2, 1)], allDoneBefore: false, ByOwner));
        Assert.Empty(HarvestPlanRules.Settle(Assigned, [Open()], allDoneBefore: false, ByOwner));
        Assert.Empty(HarvestPlanRules.Settle(Assigned, [Done(), Open()], allDoneBefore: false, ByManager));
        // Harvesting stays Harvesting even when every line is done: the Owner completes it.
        Assert.Empty(HarvestPlanRules.Settle(Harvesting, [Done()], allDoneBefore: false, ByOwner));
        // A completed plan that was whole opens again when a line is open again.
        Assert.Equal([Harvesting], HarvestPlanRules.Settle(Completed, [Done(), Open()], allDoneBefore: true, ByOwner));
    }

    [Fact]
    public void The_buttons_of_an_owner_plan_are_harvest_skip_and_finish_and_never_receive_or_hand_over()
    {
        var assigned = HarvestPlanRules.AllowedActions(Assigned, Owner, hasLot: false, canHarvestNow: true, mayHarvestInPlan: true, allLinesDone: false, ByOwner);
        var harvestingDone = HarvestPlanRules.AllowedActions(Harvesting, Owner, hasLot: true, canHarvestNow: false, mayHarvestInPlan: true, allLinesDone: true, ByOwner);
        var harvestingOpen = HarvestPlanRules.AllowedActions(Harvesting, Owner, hasLot: true, canHarvestNow: true, mayHarvestInPlan: true, allLinesDone: false, ByOwner);
        var managerAssigned = HarvestPlanRules.AllowedActions(Assigned, Owner, hasLot: false, canHarvestNow: true, mayHarvestInPlan: true, allLinesDone: false, ByManager);

        Assert.Contains(HarvestPlanAction.Harvest, assigned);
        Assert.Contains(SkipLane, assigned);
        Assert.Contains(Cancel, assigned);
        Assert.DoesNotContain(Receive, assigned);
        Assert.Equal([Edit, Finish], harvestingDone);
        Assert.DoesNotContain(HandOver, harvestingDone);
        Assert.DoesNotContain(Finish, harvestingOpen);
        Assert.Contains(HarvestPlanAction.Harvest, harvestingOpen);
        Assert.Contains(Receive, managerAssigned);
        Assert.DoesNotContain(HarvestPlanAction.Harvest, managerAssigned);
    }
}
