using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using static OMTS.Api.IntegrationTests.Harvest.HarvestPlanTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesOrderTestKit;

namespace OMTS.Api.IntegrationTests.Harvest;

/// <summary>
/// The trader weighs beside the cooperative and agrees to the weighing (owner decision of 24/11/2026): the trader of a lot is the one trader account of the orders its harvest plan serves; they weigh
/// their own cages, see them beside the cooperative's, agree to the cooperative's completed weighing, and the lot is invoiced only with that agreement.
/// </summary>
public class TraderWeighingTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private static string Mine(Guid lotId, string? action = null) => action is null ? $"/api/me/weighings/{lotId}" : $"/api/me/weighings/{lotId}/{action}";

    private static async Task<JsonElement> SheetAsync(HttpClient client, string route) => await (await client.GetAsync(route)).ReadAsync(HttpStatusCode.OK);

    private static Task<HttpResponseMessage> AgreeAsync(HttpClient trader, Guid lotId, JsonElement sheet) =>
        trader.PostAsJsonAsync(Mine(lotId, "agree"), new { rowVersion = sheet.Text("rowVersion") });

    /// <summary>A sale setup, a trader with an order of theirs, and the line of an assigned plan of the raft (lane 3) that serves the order.</summary>
    private async Task<(Sales.SaleSetup Sale, Sales.TraderActor Buyer, Guid LineId)> TraderPlanAsync(params Guid[] moreOrders)
    {
        var sale = await factory.SaleSetupAsync();
        var buyer = await Sales.PurchaseRequestTestKit.TraderAsync(factory);
        var owner = sale.Owner.Client;
        var order = (await owner.CreateOrderAsync(OrderBody(buyer.Profile.Id, sale.ProductId, await owner.BaseUnitIdAsync(sale.ProductId)))).GetProperty("order");
        var plan = await owner.AssignedPlanAsync(PlanBody(PlanLine(sale.RaftId, [3])).WithPlanField("salesOrderIds", new[] { order.Id() }.Concat(moreOrders).ToArray()));
        return (sale, buyer, plan.LineId());
    }

    private static Task<HttpResponseMessage> SellLotAsync(Sales.SaleSetup sale, Guid customerId, Guid lotId) =>
        sale.Manager.Client.PostInvoiceAsync(Invoice(customerId, [Line(sale.ProductId, lotId, 1m)], Cash(ListPrice), lotId));

    [Fact]
    public async Task The_trader_weighs_beside_the_cooperative_and_the_lot_is_invoiced_only_once_they_agree()
    {
        var (sale, buyer, lineId) = await TraderPlanAsync();
        var lotId = (await sale.Owner.Client.HarvestLotAsync(sale.RaftId, HarvestFields.OfLanes(3).FromPlanLine(lineId))).Id();

        var staff = await SheetAsync(sale.Owner.Client, $"/api/harvest-lots/{lotId}/cages");
        Assert.Equal((buyer.Profile.Name, true, false), (staff.Text("traderName"), staff.GetProperty("needsAgreement").GetBoolean(), staff.GetProperty("canAgree").GetBoolean()));
        Assert.Equal(JsonValueKind.Null, staff.GetProperty("agreement").ValueKind);
        Assert.Single(staff.GetProperty("orderCodes").EnumerateArray());

        // A cage sent again after a lost answer is weighed once.
        var operation = Guid.NewGuid();
        (await buyer.Client.PostAsJsonAsync(Mine(lotId, "cages"), new { kg = 12.5m, clientOperationId = operation })).EnsureSuccessStatusCode();
        var mine = await (await buyer.Client.PostAsJsonAsync(Mine(lotId, "cages"), new { kg = 12.5m, clientOperationId = operation })).ReadAsync(HttpStatusCode.OK);
        Assert.Equal((1, 12.5m, true), (mine.GetProperty("traderCages").GetArrayLength(), mine.Kg("traderCagesKg"), mine.GetProperty("canAgree").GetBoolean()));
        Assert.Equal(12.5m, (await SheetAsync(sale.Owner.Client, $"/api/harvest-lots/{lotId}/cages")).Kg("traderCagesKg"));

        var row = (await buyer.Client.GetFromJsonAsync<JsonElement>("/api/me/weighings")).GetProperty("items").EnumerateArray().Single();
        Assert.Equal((lotId, true, false, 1), (row.GetProperty("lotId").GetGuid(), row.GetProperty("needsAgreement").GetBoolean(), row.GetProperty("agreed").GetBoolean(), row.GetProperty("traderCageCount").GetInt32()));

        var refused = await (await SellLotAsync(sale, buyer.Profile.Id, lotId)).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal(("MSG-GEN-09", "WEIGHING_NOT_AGREED", staff.Text("lotCode")), (refused.Text("code"), refused.Text("reasonCode"), refused.Text("lotCode")));

        // The trader agrees to the cooperative's kg, whatever their own cages say; a retry after a lost answer finds the agreement.
        var agreed = await (await AgreeAsync(buyer.Client, lotId, mine)).ReadAsync(HttpStatusCode.OK);
        Assert.Equal((staff.Kg("weighedKg"), false, false), (agreed.GetProperty("agreement").Kg("agreedKg"), agreed.GetProperty("needsAgreement").GetBoolean(), agreed.GetProperty("canAgree").GetBoolean()));
        (await AgreeAsync(buyer.Client, lotId, mine)).EnsureSuccessStatusCode();
        Assert.True((await buyer.Client.GetFromJsonAsync<JsonElement>("/api/me/weighings")).GetProperty("items")[0].GetProperty("agreed").GetBoolean());

        await (await SellLotAsync(sale, buyer.Profile.Id, lotId)).ReadAsync(HttpStatusCode.Created);
    }

    [Fact]
    public async Task The_trader_agrees_only_to_a_completed_weighing_and_again_after_it_is_completed_at_another_kg()
    {
        var (sale, buyer, lineId) = await TraderPlanAsync();
        var owner = sale.Owner.Client;
        var lotId = (await owner.RecordLotAsync(sale.RaftId, HarvestFields.OfLanes(3).FromPlanLine(lineId))).Id();

        // Both weigh while the lot awaits its weighing; nothing to agree to yet.
        (await buyer.Client.PostAsJsonAsync(Mine(lotId, "cages"), new { kg = 40m })).EnsureSuccessStatusCode();
        var waiting = await (await owner.PostCageAsync(lotId.ToString(), null, 41m)).ReadAsync(HttpStatusCode.OK);
        Assert.False((await SheetAsync(buyer.Client, Mine(lotId))).GetProperty("canAgree").GetBoolean());
        Assert.Equal("MSG-GEN-09", await (await AgreeAsync(buyer.Client, lotId, waiting)).ErrorCodeAsync());

        (await owner.PostAsJsonAsync($"/api/harvest-lots/{lotId}/weigh", new { rowVersion = waiting.Text("rowVersion") })).EnsureSuccessStatusCode();
        var completed = await SheetAsync(buyer.Client, Mine(lotId));
        Assert.Equal((41m, 40m, true), (completed.Kg("weighedKg"), completed.Kg("traderCagesKg"), completed.GetProperty("canAgree").GetBoolean()));
        (await AgreeAsync(buyer.Client, lotId, completed)).EnsureSuccessStatusCode();

        // The cooperative corrects its cage: until it completes again the agreement holds for the weighing the lot still has, and there is nothing new to agree to.
        var cageId = completed.GetProperty("cages")[0].Id();
        var corrected = await (await owner.PutAsJsonAsync($"/api/harvest-lots/{lotId}/cages/{cageId}", new { kg = 40m })).ReadAsync(HttpStatusCode.OK);
        Assert.Equal((true, false), (corrected.GetProperty("needsCompleting").GetBoolean(), corrected.GetProperty("needsAgreement").GetBoolean()));

        (await owner.PostAsJsonAsync($"/api/harvest-lots/{lotId}/weigh", new { rowVersion = corrected.Text("rowVersion") })).EnsureSuccessStatusCode();
        var again = await SheetAsync(buyer.Client, Mine(lotId));
        Assert.Equal((JsonValueKind.Null, true, true), (again.GetProperty("agreement").ValueKind, again.GetProperty("needsAgreement").GetBoolean(), again.GetProperty("canAgree").GetBoolean()));
        Assert.Equal(40m, (await (await AgreeAsync(buyer.Client, lotId, again)).ReadAsync(HttpStatusCode.OK)).GetProperty("agreement").Kg("agreedKg"));
    }

    [Fact]
    public async Task Only_the_trader_of_the_lot_reads_and_weighs_it_and_staff_do_not_use_these_routes()
    {
        var (sale, buyer, lineId) = await TraderPlanAsync();
        var lotId = (await sale.Owner.Client.HarvestLotAsync(sale.RaftId, HarvestFields.OfLanes(3).FromPlanLine(lineId))).Id();
        var other = await Sales.PurchaseRequestTestKit.TraderAsync(factory);

        Assert.Equal(HttpStatusCode.NotFound, (await other.Client.GetAsync(Mine(lotId))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Client.PostAsJsonAsync(Mine(lotId, "cages"), new { kg = 10m })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Client.PostAsJsonAsync(Mine(lotId, "agree"), new { rowVersion = "" })).StatusCode);
        Assert.Empty((await other.Client.GetFromJsonAsync<JsonElement>("/api/me/weighings")).GetProperty("items").EnumerateArray());

        // A lot harvested for a walk-in order has no trader: no trader sees it, and it needs no agreement.
        Assert.Equal(HttpStatusCode.NotFound, (await buyer.Client.GetAsync(Mine(sale.LotId))).StatusCode);
        var walkIn = await SheetAsync(sale.Owner.Client, $"/api/harvest-lots/{sale.LotId}/cages");
        Assert.Equal((JsonValueKind.Null, false), (walkIn.GetProperty("traderName").ValueKind, walkIn.GetProperty("needsAgreement").GetBoolean()));

        Assert.Equal("MSG-GEN-01", await (await buyer.Client.PostAsJsonAsync(Mine(lotId, "cages"), new { kg = 0m })).ErrorCodeAsync());
        Assert.Equal("MSG-GEN-08", await (await sale.Manager.Client.GetAsync("/api/me/weighings")).ErrorCodeAsync());
        Assert.Equal("MSG-GEN-08", await (await sale.Owner.Client.GetAsync(Mine(lotId))).ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync("/api/me/weighings")).StatusCode);
    }

    [Fact]
    public async Task A_lot_harvested_for_orders_of_two_traders_has_no_trader_and_needs_no_agreement()
    {
        var other = await Sales.PurchaseRequestTestKit.TraderAsync(factory);
        var sale = await factory.SaleSetupAsync();
        var owner = sale.Owner.Client;
        var unit = await owner.BaseUnitIdAsync(sale.ProductId);
        var buyer = await Sales.PurchaseRequestTestKit.TraderAsync(factory);
        var orders = new[]
        {
            (await owner.CreateOrderAsync(OrderBody(buyer.Profile.Id, sale.ProductId, unit))).GetProperty("order").Id(),
            (await owner.CreateOrderAsync(OrderBody(other.Profile.Id, sale.ProductId, unit))).GetProperty("order").Id(),
        };
        var plan = await owner.AssignedPlanAsync(PlanBody(PlanLine(sale.RaftId, [3])).WithPlanField("salesOrderIds", orders));
        var lotId = (await owner.HarvestLotAsync(sale.RaftId, HarvestFields.OfLanes(3).FromPlanLine(plan.LineId()))).Id();

        var staff = await SheetAsync(owner, $"/api/harvest-lots/{lotId}/cages");
        Assert.Equal((JsonValueKind.Null, false, 2), (staff.GetProperty("traderName").ValueKind, staff.GetProperty("needsAgreement").GetBoolean(), staff.GetProperty("orderCodes").GetArrayLength()));
        Assert.Equal(HttpStatusCode.NotFound, (await buyer.Client.GetAsync(Mine(lotId))).StatusCode);
        await (await SellLotAsync(sale, buyer.Profile.Id, lotId)).ReadAsync(HttpStatusCode.Created);
    }
}
