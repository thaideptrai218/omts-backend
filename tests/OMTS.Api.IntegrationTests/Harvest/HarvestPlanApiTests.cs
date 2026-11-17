using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Constants;
using static OMTS.Api.IntegrationTests.Harvest.HarvestPlanTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesOrderTestKit;

namespace OMTS.Api.IntegrationTests.Harvest;

/// <summary>
/// The harvest plans through the API: codes the user types, the Owner's right and the Zone Manager's view (never a Draft), a lane in only one open plan, the hand-over notice, "Không thu" with its notice,
/// the lines that lock, and the link to orders.
/// </summary>
public class HarvestPlanApiTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task A_plan_is_saved_as_a_draft_with_a_generated_code_and_its_lanes_still_planned()
    {
        var raft = await factory.SeededRaftAsync();
        var plan = await raft.Owner.Client.CreatePlanAsync(PlanBody(PlanLine(raft.RaftId, [1, 2, 3], cages: 12)));

        Assert.Equal(StatusIds.HarvestPlan.Draft, plan.StatusId());
        Assert.Matches("^KHT[0-9]{6}$", plan.Text("code"));
        Assert.Equal("Planned", plan.LaneState(0, 2));
        Assert.Equal(3, plan.GetProperty("lines")[0].GetProperty("lanes").GetArrayLength());
        Assert.Equal(12, plan.GetProperty("lines")[0].GetProperty("expectedCageCount").GetInt32());
        Assert.Equal(["Edit", "Assign", "Cancel"], plan.AllowedActions());
    }

    [Fact]
    public async Task A_typed_code_is_kept_and_a_second_plan_with_the_same_code_in_other_letter_case_is_refused()
    {
        var raft = await factory.SeededRaftAsync();
        var client = raft.Owner.Client;
        var code = $"KHT-{Guid.NewGuid():N}"[..14];
        await client.CreatePlanAsync(PlanBody(PlanLine(raft.RaftId, [1])).WithPlanField("code", code));

        var again = await client.PostPlanAsync(PlanBody(PlanLine(raft.RaftId, [2])).WithPlanField("code", code.ToLowerInvariant()));

        var body = await again.PlanProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-02");
        Assert.Equal("Code", body.Text("field"));
        Assert.Equal(1, await factory.PlanCountAsync(code));
    }

    [Fact]
    public async Task A_plan_with_no_line_or_a_lane_given_twice_names_the_fields()
    {
        var raft = await factory.SeededRaftAsync();
        var client = raft.Owner.Client;

        var none = await client.PostPlanAsync(PlanBody());
        var twice = await client.PostPlanAsync(PlanBody(PlanLine(raft.RaftId, [1, 1])));
        var noRaftLane = await client.PostPlanAsync(PlanBody(PlanLine(raft.RaftId, [9])));

        Assert.Equal(["Lines"], await none.InvalidFieldsAsync());
        Assert.Equal(["Line 1 lanes"], await twice.InvalidFieldsAsync());
        Assert.Equal(["Line 1 lanes"], await noRaftLane.InvalidFieldsAsync());
    }

    [Fact]
    public async Task A_lane_in_another_open_plan_is_refused_until_that_plan_is_cancelled()
    {
        var raft = await factory.SeededRaftAsync();
        var client = raft.Owner.Client;
        var first = await client.CreatePlanAsync(PlanBody(PlanLine(raft.RaftId, [1, 2])));

        var clash = await client.PostPlanAsync(PlanBody(PlanLine(raft.RaftId, [2, 3])));

        var body = await clash.PlanProblemAsync(HttpStatusCode.Conflict, "MSG-PLAN-02");
        Assert.Contains(first.Text("code"), body.Text("detail"));
        (await client.PostAsJsonAsync($"{PlanRoute}/{first.Id()}/cancel", new { reason = "Planned again" })).EnsureSuccessStatusCode();
        await client.CreatePlanAsync(PlanBody(PlanLine(raft.RaftId, [2, 3])));
    }

    [Fact]
    public async Task A_zone_manager_does_not_see_a_draft_and_cannot_make_or_change_a_plan()
    {
        var raft = await factory.SeededRaftAsync();
        var manager = await factory.ManagerAsync(raft.ZoneId);
        var draft = await raft.Owner.Client.CreatePlanAsync(PlanBody(PlanLine(raft.RaftId, [1])));

        var hidden = await manager.Client.GetAsync($"{PlanRoute}/{draft.Id()}");
        var list = await manager.Client.GetFromJsonAsync<JsonElement>($"{PlanRoute}?pageSize=100");
        var create = await manager.Client.PostPlanAsync(PlanBody(PlanLine(raft.RaftId, [2])));

        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        Assert.DoesNotContain(list.GetProperty("items").EnumerateArray(), p => p.Id() == draft.Id());
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
    }

    [Fact]
    public async Task A_handed_over_plan_is_seen_by_the_zone_manager_who_is_told_and_may_harvest_and_leave_lanes_out()
    {
        var raft = await factory.SeededRaftAsync();
        var manager = await factory.ManagerAsync(raft.ZoneId);
        var plan = await raft.Owner.Client.AssignedPlanAsync(PlanBody(PlanLine(raft.RaftId, [1, 2])));

        var seen = await manager.Client.GetPlanAsync(plan.Id());
        var assigned = await manager.Client.GetFromJsonAsync<JsonElement>($"{PlanRoute}/assigned");

        Assert.Equal(StatusIds.HarvestPlan.Assigned, seen.StatusId());
        Assert.Equal(["Harvest", "SkipLane"], seen.AllowedActions());
        Assert.Contains(assigned.GetProperty("plans").EnumerateArray(), p => p.Id() == plan.Id());
        var notices = await factory.NoticesAsync(NotificationTypes.HarvestPlanAssigned, plan.Id());
        Assert.Contains(notices, n => n.RecipientAccountId == manager.Account.Id && n.MessageCode == "MSG-PLAN-05");
    }

    [Fact]
    public async Task A_plan_whose_raft_has_no_open_cycle_cannot_be_handed_over()
    {
        var raft = await factory.SeededRaftAsync();
        var plan = await raft.Owner.Client.CreatePlanAsync(PlanBody(PlanLine(raft.RaftId, [1])));
        await factory.CloseCycleAsync(raft.CycleId);

        var response = await raft.Owner.Client.PostAsJsonAsync($"{PlanRoute}/{plan.Id()}/assign", new { });

        await response.PlanProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
    }

    [Fact]
    public async Task Cancelling_needs_a_reason_and_a_plan_that_has_started_is_finished_not_cancelled()
    {
        var raft = await factory.SeededRaftAsync();
        var client = raft.Owner.Client;
        var plan = await client.AssignedPlanAsync(PlanBody(PlanLine(raft.RaftId, [1]), PlanLine(raft.RaftId, [2])));
        Assert.Equal("Reason", await (await client.PostAsJsonAsync($"{PlanRoute}/{plan.Id()}/cancel", new { reason = " " })).PlanFieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        (await client.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1).FromPlanLine(plan.LineId(0)))).GetProperty("id").GetGuid();

        var cancel = await client.PostAsJsonAsync($"{PlanRoute}/{plan.Id()}/cancel", new { reason = "Changed mind" });

        await cancel.PlanProblemAsync(HttpStatusCode.Conflict, "MSG-PLAN-04");
        var finished = await client.PlanCommandAsync(plan.Id(), "finish");
        Assert.Equal(StatusIds.HarvestPlan.Completed, finished.StatusId());
        Assert.Equal("Planned", finished.LaneState(1, 2));
    }

    [Fact]
    public async Task After_the_hand_over_a_line_without_a_lot_changes_and_a_line_is_added_but_a_locked_line_must_stay_as_it_is()
    {
        var raft = await factory.SeededRaftAsync();
        var client = raft.Owner.Client;
        var manager = await factory.ManagerAsync(raft.ZoneId);
        var plan = await client.AssignedPlanAsync(PlanBody(PlanLine(raft.RaftId, [1]), PlanLine(raft.RaftId, [2])));
        (await manager.Client.PostSkipAsync(plan.Id(), plan.LineId(0), [1], "Dead oysters")).EnsureSuccessStatusCode();
        var current = await client.GetPlanAsync(plan.Id());

        Dictionary<string, object?> Body(params object[] lines) => PlanBody(lines).WithPlanField("rowVersion", current.Text("rowVersion"));
        var lockedChanged = await client.PutAsJsonAsync($"{PlanRoute}/{plan.Id()}", Body(PlanLine(raft.RaftId, [1, 3], plan.LineId(0)), PlanLine(raft.RaftId, [2], plan.LineId(1))));
        var lockedDropped = await client.PutAsJsonAsync($"{PlanRoute}/{plan.Id()}", Body(PlanLine(raft.RaftId, [2], plan.LineId(1))));
        var fine = await client.PutAsJsonAsync($"{PlanRoute}/{plan.Id()}", Body(PlanLine(raft.RaftId, [1], plan.LineId(0)), PlanLine(raft.RaftId, [4], plan.LineId(1)), PlanLine(raft.RaftId, [5])));

        await lockedChanged.PlanProblemAsync(HttpStatusCode.Conflict, "MSG-PLAN-03");
        await lockedDropped.PlanProblemAsync(HttpStatusCode.Conflict, "MSG-PLAN-03");
        var saved = (await fine.ReadAsync(HttpStatusCode.OK)).GetProperty("plan");
        Assert.Equal(3, saved.GetProperty("lines").GetArrayLength());
        Assert.Equal("Skipped", saved.LaneState(0, 1));
        Assert.Equal(["Planned"], saved.GetProperty("lines")[1].GetProperty("lanes").EnumerateArray().Select(l => l.Text("state")).ToList());
    }

    [Fact]
    public async Task Leaving_lanes_out_needs_a_reason_starts_the_harvest_and_tells_the_owner_and_can_be_undone_while_the_line_has_no_lot()
    {
        var raft = await factory.SeededRaftAsync();
        var manager = await factory.ManagerAsync(raft.ZoneId);
        var plan = await raft.Owner.Client.AssignedPlanAsync(PlanBody(PlanLine(raft.RaftId, [1, 2]), PlanLine(raft.RaftId, [3])));
        var line = plan.LineId(0);

        var noReason = await manager.Client.PostSkipAsync(plan.Id(), line, [1], null);
        var skipped = await (await manager.Client.PostSkipAsync(plan.Id(), line, [1], "Too small")).ReadAsync(HttpStatusCode.OK);

        Assert.Equal("Reason", await noReason.PlanFieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        Assert.Equal((StatusIds.HarvestPlan.Harvesting, "Skipped"), (skipped.StatusId(), skipped.LaneState(0, 1)));
        Assert.Equal("Too small", skipped.GetProperty("lines")[0].GetProperty("lanes")[0].Text("skipReason"));
        var notices = await factory.NoticesAsync(NotificationTypes.HarvestPlanLanesSkipped, plan.Id());
        Assert.Contains(notices, n => n.RecipientAccountId == raft.Owner.Account.Id && n.MessageCode == "MSG-PLAN-06");

        var undone = await (await manager.Client.PostSkipAsync(plan.Id(), line, [1], null, "unskip-lanes")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal("Planned", undone.LaneState(0, 1));
    }

    [Fact]
    public async Task Every_lane_left_out_completes_a_plan_that_had_no_lot()
    {
        var raft = await factory.SeededRaftAsync();
        var plan = await raft.Owner.Client.AssignedPlanAsync(PlanBody(PlanLine(raft.RaftId, [1, 2])));

        var done = await (await raft.Owner.Client.PostSkipAsync(plan.Id(), plan.LineId(), [1, 2], "Storm")).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(StatusIds.HarvestPlan.Completed, done.StatusId());
        Assert.Equal("Skipped", done.GetProperty("lines")[0].Text("state"));
        var undone = await (await raft.Owner.Client.PostSkipAsync(plan.Id(), plan.LineId(), [2], null, "unskip-lanes")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(StatusIds.HarvestPlan.Harvesting, undone.StatusId());
    }

    [Fact]
    public async Task A_plan_serves_orders_and_the_order_shows_it_with_a_draft_prefilled_from_the_order()
    {
        var sale = await factory.SaleSetupAsync();
        var client = sale.Owner.Client;
        var unit = await client.BaseUnitIdAsync(sale.ProductId);
        var order = (await client.CreateOrderAsync(OrderBody(sale.CustomerId, sale.ProductId, unit, quantity: 40m))).GetProperty("order");

        var draft = await (await client.GetAsync($"{PlanRoute}/order-draft/{order.Id()}")).ReadAsync(HttpStatusCode.OK);
        var plan = await client.CreatePlanAsync(PlanBody(PlanLine(sale.RaftId, [3])).WithPlanField("salesOrderIds", new[] { order.Id() }));
        var again = await client.GetOrderAsync(order.Id());

        Assert.Equal(order.Text("code"), draft.GetProperty("salesOrder").Text("code"));
        Assert.False(draft.TryGetProperty("zone", out _));
        Assert.Equal(40m, draft.GetProperty("products")[0].GetProperty("quantity").GetDecimal());
        Assert.Equal(order.Text("code"), Assert.Single(plan.GetProperty("orders").EnumerateArray()).Text("code"));
        var linked = Assert.Single(again.GetProperty("harvestPlans").EnumerateArray());
        Assert.Equal((plan.Id(), plan.Text("code")), (linked.Id(), linked.Text("code")));
    }

    [Fact]
    public async Task One_plan_links_many_orders_and_one_order_many_plans()
    {
        var sale = await factory.SaleSetupAsync();
        var client = sale.Owner.Client;
        var unit = await client.BaseUnitIdAsync(sale.ProductId);
        var first = (await client.CreateOrderAsync(OrderBody(sale.CustomerId, sale.ProductId, unit))).GetProperty("order");
        var second = (await client.CreateOrderAsync(OrderBody(sale.CustomerId, sale.ProductId, unit))).GetProperty("order");

        var planOne = await client.CreatePlanAsync(PlanBody(PlanLine(sale.RaftId, [3])).WithPlanField("salesOrderIds", new[] { first.Id(), second.Id() }));
        var planTwo = await client.CreatePlanAsync(PlanBody(PlanLine(sale.RaftId, [4])).WithPlanField("salesOrderIds", new[] { first.Id() }));

        Assert.Equal(2, planOne.GetProperty("orders").GetArrayLength());
        var codes = (await client.GetOrderAsync(first.Id())).GetProperty("harvestPlans").EnumerateArray().Select(p => p.Text("code")).Order().ToList();
        Assert.Equal(new[] { planOne.Text("code"), planTwo.Text("code") }.Order().ToList(), codes);
    }

    [Fact]
    public async Task A_plan_spans_rafts_of_many_zones_and_each_zone_manager_sees_and_is_told_of_its_own_lines_only()
    {
        var first = await factory.SeededRaftAsync();
        var second = await factory.SeededRaftAsync();
        var managerOne = await factory.ManagerAsync(first.ZoneId);
        var managerTwo = await factory.ManagerAsync(second.ZoneId);

        var plan = await first.Owner.Client.AssignedPlanAsync(PlanBody(PlanLine(first.RaftId, [1, 2]), PlanLine(second.RaftId, [3])));

        Assert.Equal(2, plan.GetProperty("zones").GetArrayLength());
        Assert.Equal(new[] { first.ZoneId, second.ZoneId }.Order(), plan.GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("zone").Id()).Order());
        var seenOne = await managerOne.Client.GetPlanAsync(plan.Id());
        var seenTwo = await managerTwo.Client.GetPlanAsync(plan.Id());
        Assert.Equal([first.RaftId], seenOne.GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("raftId").GetGuid()));
        Assert.Equal([second.RaftId], seenTwo.GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("raftId").GetGuid()));
        Assert.Equal(first.ZoneId, Assert.Single(seenOne.GetProperty("zones").EnumerateArray()).Id());
        var notices = await factory.NoticesAsync(NotificationTypes.HarvestPlanAssigned, plan.Id());
        Assert.Contains(notices, n => n.RecipientAccountId == managerOne.Account.Id);
        Assert.Contains(notices, n => n.RecipientAccountId == managerTwo.Account.Id);
        // A line of another zone is not there for a Zone Manager: leaving its lanes out answers 404.
        var foreign = await managerOne.Client.PostSkipAsync(plan.Id(), plan.LineId(1), [3], "Not mine");
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
    }

    [Fact]
    public async Task A_zone_manager_does_not_see_a_plan_that_has_no_line_in_its_zones()
    {
        var raft = await factory.SeededRaftAsync();
        var other = await factory.SeededRaftAsync();
        var outsider = await factory.ManagerAsync(other.ZoneId);
        var plan = await raft.Owner.Client.AssignedPlanAsync(PlanBody(PlanLine(raft.RaftId, [1])));

        var response = await outsider.Client.GetAsync($"{PlanRoute}/{plan.Id()}");
        var list = await outsider.Client.GetFromJsonAsync<JsonElement>($"{PlanRoute}?pageSize=100");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain(list.GetProperty("items").EnumerateArray(), p => p.Id() == plan.Id());
    }

    [Fact]
    public async Task The_eligible_rafts_name_their_zone_and_lanes_and_show_a_lane_waiting_in_another_plan()
    {
        var raft = await factory.SeededRaftAsync();
        var plan = await raft.Owner.Client.CreatePlanAsync(PlanBody(PlanLine(raft.RaftId, [2])));

        var all = await raft.Owner.Client.GetFromJsonAsync<JsonElement>($"{PlanRoute}/eligible-rafts");
        var ofZone = await raft.Owner.Client.GetFromJsonAsync<JsonElement>($"{PlanRoute}/eligible-rafts?zoneId={raft.ZoneId}");
        var manager = await factory.ManagerAsync(raft.ZoneId);

        var found = all.GetProperty("rafts").EnumerateArray().Single(r => r.GetProperty("raftId").GetGuid() == raft.RaftId);
        Assert.Equal(raft.ZoneId, found.GetProperty("zone").Id());
        var lanes = found.GetProperty("lanes").EnumerateArray().ToDictionary(l => l.GetProperty("laneNumber").GetInt32());
        Assert.Equal(("InAnotherPlan", plan.Text("code")), (lanes[2].Text("state"), lanes[2].Text("planCode")));
        Assert.Equal("Free", lanes[1].Text("state"));
        Assert.All(ofZone.GetProperty("rafts").EnumerateArray(), r => Assert.Equal(raft.ZoneId, r.GetProperty("zone").Id()));
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.Client.GetAsync($"{PlanRoute}/eligible-rafts")).StatusCode);
    }
}
