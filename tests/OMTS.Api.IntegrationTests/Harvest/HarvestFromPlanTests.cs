using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Domain.Constants;
using static OMTS.Api.IntegrationTests.Harvest.HarvestPlanTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesOrderTestKit;

namespace OMTS.Api.IntegrationTests.Harvest;

/// <summary>A Zone Manager records lots from the lines of a harvest plan: the raft and lanes are the plan's, the lot carries the plan, the plan moves along, and a removed lot opens its line again.</summary>
public class HarvestFromPlanTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task A_lot_recorded_from_a_line_holds_the_lanes_of_the_plan_whatever_lanes_were_sent_and_carries_the_plan()
    {
        var raft = await factory.SeededRaftAsync();
        var manager = await factory.ManagerAsync(raft.ZoneId);
        var plan = await raft.Owner.Client.AssignedPlanAsync(PlanBody(PlanLine(raft.RaftId, [1, 2]), PlanLine(raft.RaftId, [4])));

        var saved = await manager.Client.RecordHarvestAsync(raft.RaftId, HarvestFields.OfLanes(5).FromPlanLine(plan.LineId(0)));

        var lot = saved.GetProperty("lot");
        Assert.Equal([1, 2], lot.GetProperty("laneNumbers").EnumerateArray().Select(l => l.GetInt32()));
        Assert.Equal((plan.Id(), plan.Text("code")), (lot.GetProperty("harvestPlan").Id(), lot.GetProperty("harvestPlan").Text("code")));
        Assert.Matches("^.+-[0-9]{8}", lot.Text("lotCode"));
        var after = await raft.Owner.Client.GetPlanAsync(plan.Id());
        Assert.Equal(StatusIds.HarvestPlan.Harvesting, after.StatusId());
        Assert.Equal(lot.Text("lotCode"), after.GetProperty("lines")[0].GetProperty("lot").Text("lotCode"));
        Assert.Equal("Harvested", after.LaneState(0, 1));
        var again = await manager.Client.GetHarvestLotAsync(lot.Id());
        Assert.Equal(plan.Text("code"), again.GetProperty("harvestPlan").Text("code"));
    }

    [Fact]
    public async Task The_last_line_recorded_completes_the_plan_and_the_owner_is_told_the_plan_code_with_the_lot()
    {
        var raft = await factory.SeededRaftAsync();
        var manager = await factory.ManagerAsync(raft.ZoneId);
        var plan = await raft.Owner.Client.AssignedPlanAsync(PlanBody(PlanLine(raft.RaftId, [1]), PlanLine(raft.RaftId, [2])));

        var first = await manager.Client.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1).FromPlanLine(plan.LineId(0)));
        await manager.Client.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(2).FromPlanLine(plan.LineId(1)));

        Assert.Equal(StatusIds.HarvestPlan.Completed, (await raft.Owner.Client.GetPlanAsync(plan.Id())).StatusId());
        var notices = await factory.NoticesAsync(Application.Features.Notifications.NotificationTypes.HarvestLotRecorded, first.Id());
        Assert.Contains(notices, n => n.RecipientAccountId == raft.Owner.Account.Id && n.MessageParams is { } p && p.Contains(plan.Text("code")));
    }

    [Fact]
    public async Task A_lane_left_out_is_not_in_the_lot()
    {
        var raft = await factory.SeededRaftAsync();
        var manager = await factory.ManagerAsync(raft.ZoneId);
        var plan = await raft.Owner.Client.AssignedPlanAsync(PlanBody(PlanLine(raft.RaftId, [1, 2, 3])));
        (await manager.Client.PostSkipAsync(plan.Id(), plan.LineId(), [2], "Empty lane")).EnsureSuccessStatusCode();

        var lot = await manager.Client.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(2).FromPlanLine(plan.LineId()));

        Assert.Equal([1, 3], lot.GetProperty("laneNumbers").EnumerateArray().Select(l => l.GetInt32()));
        var done = await raft.Owner.Client.GetPlanAsync(plan.Id());
        Assert.Equal(("Harvested", "Skipped", "Harvested"), (done.LaneState(0, 1), done.LaneState(0, 2), done.LaneState(0, 3)));
        Assert.Equal(StatusIds.HarvestPlan.Completed, done.StatusId());
    }

    [Fact]
    public async Task A_line_that_has_its_lot_cannot_be_recorded_again_nor_have_lanes_left_out()
    {
        var raft = await factory.SeededRaftAsync();
        var manager = await factory.ManagerAsync(raft.ZoneId);
        var plan = await raft.Owner.Client.AssignedPlanAsync(PlanBody(PlanLine(raft.RaftId, [1]), PlanLine(raft.RaftId, [2])));
        await manager.Client.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1).FromPlanLine(plan.LineId(0)));

        var again = await manager.Client.PostHarvestAsync(raft.RaftId, HarvestFields.OfLanes(1).FromPlanLine(plan.LineId(0)));
        var skip = await manager.Client.PostSkipAsync(plan.Id(), plan.LineId(0), [1], "Late");

        await again.PlanProblemAsync(HttpStatusCode.Conflict, "MSG-PLAN-03");
        await skip.PlanProblemAsync(HttpStatusCode.Conflict, "MSG-PLAN-03");
        Assert.Equal(1, await factory.LotCountAsync(raft.RaftId));
    }

    [Fact]
    public async Task A_line_of_another_raft_a_draft_plan_or_an_unreadable_id_records_nothing()
    {
        var raft = await factory.SeededRaftAsync();
        var other = await factory.SeededRaftAsync();
        var draft = await raft.Owner.Client.CreatePlanAsync(PlanBody(PlanLine(raft.RaftId, [1])));
        var assigned = await raft.Owner.Client.AssignedPlanAsync(PlanBody(PlanLine(raft.RaftId, [2])));

        var draftTry = await raft.Owner.Client.PostHarvestAsync(raft.RaftId, HarvestFields.OfLanes(1).FromPlanLine(draft.LineId()));
        var wrongRaft = await other.Owner.Client.PostHarvestAsync(other.RaftId, HarvestFields.OfLanes(2).FromPlanLine(assigned.LineId()));
        var form = HarvestFields.OfLanes(2).ToForm();
        form.Add(new StringContent("not-an-id"), "harvestPlanLineId");
        var unreadable = await raft.Owner.Client.PostAsync($"/api/rafts/{raft.RaftId}/harvest-lots", form);

        await draftTry.PlanProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
        Assert.Equal("Harvest plan line", await wrongRaft.PlanFieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        Assert.Equal("Harvest plan line", await unreadable.PlanFieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        Assert.Equal(0, await factory.LotCountAsync(raft.RaftId));
    }

    [Fact]
    public async Task Removing_the_lot_of_a_plan_that_finished_by_itself_opens_the_line_and_the_plan_again()
    {
        var raft = await factory.SeededRaftAsync();
        var client = raft.Owner.Client;
        var plan = await client.AssignedPlanAsync(PlanBody(PlanLine(raft.RaftId, [1]), PlanLine(raft.RaftId, [2])));
        var lot = await client.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1).FromPlanLine(plan.LineId(0)));
        await client.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(2).FromPlanLine(plan.LineId(1)));
        Assert.Equal(StatusIds.HarvestPlan.Completed, (await client.GetPlanAsync(plan.Id())).StatusId());

        (await factory.SendRemovalAsync(client, lot.Id())).EnsureSuccessStatusCode();

        var after = await client.GetPlanAsync(plan.Id());
        Assert.Equal(StatusIds.HarvestPlan.Harvesting, after.StatusId());
        Assert.Equal("Open", after.GetProperty("lines")[0].Text("state"));
        Assert.Equal(JsonValueKind.Null, after.GetProperty("lines")[0].GetProperty("lot").ValueKind);
        var rerecord = await client.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1).FromPlanLine(plan.LineId(0)));
        Assert.NotEqual(lot.Id(), rerecord.Id());
    }

    [Fact]
    public async Task A_plan_the_owner_finished_by_hand_stays_completed_when_a_lot_is_removed()
    {
        var raft = await factory.SeededRaftAsync();
        var client = raft.Owner.Client;
        var plan = await client.AssignedPlanAsync(PlanBody(PlanLine(raft.RaftId, [1]), PlanLine(raft.RaftId, [2])));
        var lot = await client.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1).FromPlanLine(plan.LineId(0)));
        await client.PlanCommandAsync(plan.Id(), "finish");

        (await factory.SendRemovalAsync(client, lot.Id())).EnsureSuccessStatusCode();

        Assert.Equal(StatusIds.HarvestPlan.Completed, (await client.GetPlanAsync(plan.Id())).StatusId());
    }

    [Fact]
    public async Task A_correction_of_a_lot_of_a_plan_keeps_to_the_lanes_of_the_line()
    {
        var raft = await factory.SeededRaftAsync();
        var client = raft.Owner.Client;
        var plan = await client.AssignedPlanAsync(PlanBody(PlanLine(raft.RaftId, [1, 2])));
        var lot = await client.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1).FromPlanLine(plan.LineId()));

        var outside = await factory.SendCorrectionAsync(client, lot.Id(), HarvestFields.OfLanes(1, 2, 3));
        var inside = await factory.SendCorrectionAsync(client, lot.Id(), HarvestFields.OfLanes(1) with { Cages = "5" });

        await outside.PlanProblemAsync(HttpStatusCode.Conflict, "MSG-PLAN-01");
        await inside.ReadAsync(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_lots_for_sale_of_a_plan_that_serves_the_order_come_first_and_are_suggested()
    {
        var sale = await factory.SaleSetupAsync();
        var client = sale.Owner.Client;
        var unit = await client.BaseUnitIdAsync(sale.ProductId);
        var order = (await client.CreateOrderAsync(OrderBody(sale.CustomerId, sale.ProductId, unit))).GetProperty("order");
        var plan = await client.AssignedPlanAsync(PlanBody(PlanLine(sale.RaftId, [3])).WithPlanField("salesOrderIds", new[] { order.Id() }));
        var planned = await client.HarvestLotAsync(sale.RaftId, HarvestFields.OfLanes(3).FromPlanLine(plan.LineId()));

        var plain = await client.GetFromJsonAsync<JsonElement>($"/api/harvest-lots/for-sale?zoneId={sale.ZoneId}");
        var suggested = await client.GetFromJsonAsync<JsonElement>($"/api/harvest-lots/for-sale?zoneId={sale.ZoneId}&salesOrderId={order.Id()}");

        var lots = suggested.GetProperty("lots").EnumerateArray().ToList();
        Assert.Equal(planned.Id(), lots[0].Id());
        Assert.True(lots[0].GetProperty("suggested").GetBoolean());
        Assert.Equal(plan.Text("code"), lots[0].Text("harvestPlanCode"));
        Assert.All(lots.Skip(1), l => Assert.False(l.GetProperty("suggested").GetBoolean()));
        Assert.All(plain.GetProperty("lots").EnumerateArray(), l => Assert.False(l.GetProperty("suggested").GetBoolean()));
        Assert.Equal(lots.Count, plain.GetProperty("lots").GetArrayLength());
    }
}
