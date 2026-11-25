using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Constants;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Harvest.HarvestPlanTestKit;

namespace OMTS.Api.IntegrationTests.Harvest;

/// <summary>
/// Owner decision of 25/11/2026: when assigning a plan the Owner picks who harvests it, <c>ZoneManager</c> (the flow with the reception and the hand-over) or <c>Owner</c> (he records the lots himself and completes the
/// plan: Assigned, Harvesting, Completed). Zone Managers are not told of a plan of the Owner, do not see it and cannot act on it.
/// </summary>
public class HarvestPlanOwnerExecutorTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task A_plan_assigned_with_no_executor_is_the_zone_manager_flow_and_the_view_names_the_executor()
    {
        var raft = await factory.SeededRaftAsync();
        var draft = await raft.Owner.Client.CreatePlanAsync(PlanBody(PlanLine(raft.RaftId, [1])));

        var assigned = await raft.Owner.Client.PlanCommandAsync(draft.Id(), "assign");

        Assert.Equal("ZoneManager", draft.Text("executor"));
        Assert.Equal("ZoneManager", assigned.Text("executor"));
        Assert.Contains("Receive", assigned.AllowedActions());
        Assert.Equal(HarvestPlanExecutor.ZoneManager, await factory.QueryAsync(db => db.HarvestPlans.AsNoTracking().Where(p => p.Id == draft.Id()).Select(p => p.Executor).SingleAsync()));
    }

    [Fact]
    public async Task An_unknown_executor_is_refused_and_the_plan_stays_a_draft()
    {
        var raft = await factory.SeededRaftAsync();
        var draft = await raft.Owner.Client.CreatePlanAsync(PlanBody(PlanLine(raft.RaftId, [1])));

        var refused = await raft.Owner.Client.PostAsJsonAsync($"{PlanRoute}/{draft.Id()}/assign", new { executor = 7 });

        Assert.Equal("Executor", await refused.PlanFieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        Assert.Equal(StatusIds.HarvestPlan.Draft, (await raft.Owner.Client.GetPlanAsync(draft.Id())).StatusId());
    }

    [Fact]
    public async Task The_owner_harvests_his_plan_right_after_assigned_and_no_zone_manager_is_told_sees_or_acts_on_it()
    {
        var raft = await factory.SeededRaftAsync();
        var manager = await factory.ManagerAsync(raft.ZoneId);
        var owner = raft.Owner.Client;
        var plan = await owner.OwnerExecutedPlanAsync(PlanBody(PlanLine(raft.RaftId, [1])));

        // Assigned is ready: the Owner harvests and leaves lanes out at once; there is no Receive and no HandOver.
        Assert.Equal(("Owner", StatusIds.HarvestPlan.Assigned), (plan.Text("executor"), plan.StatusId()));
        Assert.Equal(["Edit", "Cancel", "Harvest", "SkipLane"], plan.AllowedActions());
        Assert.True(plan.GetProperty("lines")[0].GetProperty("canHarvest").GetBoolean());
        // The Zone Manager of the zone is not told, finds no plan in the list or in the plans to harvest, and the plan is not there for it.
        Assert.Empty(await factory.NoticesAsync(NotificationTypes.HarvestPlanAssigned, plan.Id()).ContinueWith(t => t.Result.Where(n => n.RecipientAccountId == manager.Account.Id)));
        Assert.Equal(HttpStatusCode.NotFound, (await manager.Client.GetAsync($"{PlanRoute}/{plan.Id()}")).StatusCode);
        var list = await manager.Client.GetFromJsonAsync<JsonElement>($"{PlanRoute}?pageSize=100");
        Assert.DoesNotContain(list.GetProperty("items").EnumerateArray(), p => p.Id() == plan.Id());
        var toHarvest = await manager.Client.GetFromJsonAsync<JsonElement>($"{PlanRoute}/assigned");
        Assert.DoesNotContain(toHarvest.GetProperty("plans").EnumerateArray(), p => p.Id() == plan.Id());
        var ownerList = await owner.GetFromJsonAsync<JsonElement>($"{PlanRoute}/assigned");
        Assert.Contains(ownerList.GetProperty("plans").EnumerateArray(), p => p.Id() == plan.Id());
        Assert.Equal(HttpStatusCode.NotFound, (await manager.Client.PostAsJsonAsync($"{PlanRoute}/{plan.Id()}/receive", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await manager.Client.PostSkipAsync(plan.Id(), plan.LineId(), [1], "Not mine")).StatusCode);
        var managerLot = await manager.Client.PostHarvestAsync(raft.RaftId, HarvestFields.OfLanes(1).FromPlanLine(plan.LineId()));
        Assert.False(managerLot.IsSuccessStatusCode, "a Zone Manager recorded a lot from the plan of the Owner");
        Assert.Equal(0, await factory.LotCountAsync(raft.RaftId));
    }

    [Fact]
    public async Task The_owner_records_the_lots_and_finishes_from_harvesting_once_every_line_is_done()
    {
        var raft = await factory.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var plan = await owner.OwnerExecutedPlanAsync(PlanBody(PlanLine(raft.RaftId, [1]), PlanLine(raft.RaftId, [2])));

        var first = await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1).FromPlanLine(plan.LineId(0)));
        var harvesting = await owner.GetPlanAsync(plan.Id());
        // Receiving and handing over do not exist for this plan, and the plan is not finished while a line is open.
        var receive = await owner.PostAsJsonAsync($"{PlanRoute}/{plan.Id()}/receive", new { });
        var handOver = await owner.PostAsJsonAsync($"{PlanRoute}/{plan.Id()}/hand-over", new { });
        var tooEarly = await owner.PostAsJsonAsync($"{PlanRoute}/{plan.Id()}/finish", new { });

        Assert.Equal(StatusIds.HarvestPlan.Harvesting, harvesting.StatusId());
        Assert.DoesNotContain("Finish", harvesting.AllowedActions());
        await receive.PlanProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
        await handOver.PlanProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
        await tooEarly.PlanProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");

        await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(2).FromPlanLine(plan.LineId(1)));
        var ready = await owner.GetPlanAsync(plan.Id());
        // The system never completes the plan: every line is done and the Owner presses "Hoàn thành".
        Assert.Equal(StatusIds.HarvestPlan.Harvesting, ready.StatusId());
        Assert.Contains("Finish", ready.AllowedActions());
        var finished = await owner.PlanCommandAsync(plan.Id(), "finish");

        Assert.Equal(StatusIds.HarvestPlan.Completed, finished.StatusId());
        Assert.Equal(first.Text("lotCode"), finished.GetProperty("lines")[0].GetProperty("lot").Text("lotCode"));
    }

    [Fact]
    public async Task Leaving_every_lane_out_starts_and_completes_an_owner_plan_and_putting_one_back_opens_it_again()
    {
        var raft = await factory.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var plan = await owner.OwnerExecutedPlanAsync(PlanBody(PlanLine(raft.RaftId, [1, 2])));

        (await owner.PostSkipAsync(plan.Id(), plan.LineId(), [1, 2], "No oysters")).EnsureSuccessStatusCode();
        var skipped = await owner.GetPlanAsync(plan.Id());

        Assert.Equal(StatusIds.HarvestPlan.Harvesting, skipped.StatusId());
        Assert.Contains("Finish", skipped.AllowedActions());
        var finished = await owner.PlanCommandAsync(plan.Id(), "finish");
        Assert.Equal(StatusIds.HarvestPlan.Completed, finished.StatusId());
        (await owner.PostSkipAsync(plan.Id(), plan.LineId(), [2], null, "unskip-lanes")).EnsureSuccessStatusCode();
        Assert.Equal(StatusIds.HarvestPlan.Harvesting, (await owner.GetPlanAsync(plan.Id())).StatusId());
    }

    [Fact]
    public async Task An_owner_plan_can_be_cancelled_before_a_lot_and_a_manager_plan_of_the_same_zone_is_unaffected()
    {
        var raft = await factory.SeededRaftAsync();
        var manager = await factory.ManagerAsync(raft.ZoneId);
        var owner = raft.Owner.Client;
        var other = await owner.AssignedPlanAsync(PlanBody(PlanLine(raft.RaftId, [4])));
        var own = await owner.OwnerExecutedPlanAsync(PlanBody(PlanLine(raft.RaftId, [5])));

        var cancelled = await owner.PostAsJsonAsync($"{PlanRoute}/{own.Id()}/cancel", new { reason = "Customer cancelled" });

        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        Assert.Equal(StatusIds.HarvestPlan.Cancelled, (await owner.GetPlanAsync(own.Id())).StatusId());
        Assert.Equal(["Receive"], (await manager.Client.GetPlanAsync(other.Id())).AllowedActions());
    }
}
