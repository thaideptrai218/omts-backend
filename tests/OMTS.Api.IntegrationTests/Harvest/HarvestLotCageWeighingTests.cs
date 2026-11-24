using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Harvest;
using SalesKit = OMTS.Api.IntegrationTests.Sales.SalesCatalogueTestKit;

namespace OMTS.Api.IntegrationTests.Harvest;

/// <summary>
/// Weighing at the port cage by cage (owner decision of 24/11/2026): each cage is saved as soon as it is weighed, the weighing sheet sums them, and completing the weighing gives the lot and its product
/// line the sum of the cages. A cage corrected later changes nothing until the weighing is completed again.
/// </summary>
public class HarvestLotCageWeighingTests
{
    private sealed record OneLineLot(AuthApiFactory Api, HttpClient Owner, Guid LotId, Guid ProductId) : IDisposable
    {
        public void Dispose() => Api.Dispose();
    }

    /// <summary>A lot awaiting its weighing with its one product line (a lot holds one kind of oyster): 3 cages, 150 kg estimated.</summary>
    private static async Task<OneLineLot> OneLineLotAsync()
    {
        var api = new AuthApiFactory();
        var raft = await api.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var product = (await SalesKit.CreateProductAsync(owner)).Id();
        var lot = await owner.RecordLotAsync(raft.RaftId, HarvestFields.OfLanes(1).WithProducts((product, 3, 150m)));
        return new OneLineLot(api, owner, lot.Id(), product);
    }

    private static Task<JsonElement> SheetAsync(HttpClient client, Guid lotId) => client.GetFromJsonAsync<JsonElement>($"/api/harvest-lots/{lotId}/cages");

    /// <summary>Weighs a cage as the app does: no product, the server takes the lot's.</summary>
    private static async Task<JsonElement> AddCageAsync(HttpClient client, Guid lotId, decimal kg) =>
        await (await client.PostCageAsync(lotId.ToString(), null, kg)).ReadAsync(HttpStatusCode.OK);

    private static async Task<HttpResponseMessage> CompleteAsync(HttpClient client, Guid lotId) =>
        await client.PostAsJsonAsync($"/api/harvest-lots/{lotId}/weigh", new { rowVersion = (await SheetAsync(client, lotId)).Text("rowVersion") });

    private static IEnumerable<int> Numbers(JsonElement sheet) => sheet.GetProperty("cages").EnumerateArray().Select(c => c.GetProperty("number").GetInt32());

    [Fact]
    public async Task Cages_are_saved_one_by_one_and_completing_gives_the_lot_the_sum_of_its_cages()
    {
        using var lot = await OneLineLotAsync();
        await AddCageAsync(lot.Owner, lot.LotId, 48.5m);
        await AddCageAsync(lot.Owner, lot.LotId, 49m);
        var sheet = await AddCageAsync(lot.Owner, lot.LotId, 51.25m);

        // Saved under the lot's product and summed, but the lot still waits for its weighing.
        Assert.Equal([1, 2, 3], Numbers(sheet));
        Assert.All(sheet.GetProperty("cages").EnumerateArray(), c => Assert.Equal(lot.ProductId, c.GetProperty("productId").GetGuid()));
        var line = sheet.GetProperty("products").EnumerateArray().Single();
        Assert.Equal((3m, 3, 148.75m), (line.Kg("harvestedCages"), line.GetProperty("weighedCages").GetInt32(), line.Kg("cagesKg")));
        Assert.Equal(148.75m, sheet.Kg("cagesKg"));
        Assert.Equal((StatusIds.HarvestLot.AwaitingWeighing, true, true), (sheet.GetProperty("statusId").GetInt32(), sheet.GetProperty("needsCompleting").GetBoolean(), sheet.GetProperty("canEdit").GetBoolean()));
        Assert.Equal(JsonValueKind.Null, sheet.GetProperty("weighedKg").ValueKind);

        var weighed = (await (await CompleteAsync(lot.Owner, lot.LotId)).ReadAsync(HttpStatusCode.OK)).GetProperty("lot");

        Assert.Equal((StatusIds.HarvestLot.Open, 148.75m), (weighed.GetProperty("statusId").GetInt32(), weighed.Kg("weighedKg")));
        Assert.Equal(148.75m, weighed.GetProperty("productLines").EnumerateArray().Single().Kg("weighedKg"));
        Assert.False((await SheetAsync(lot.Owner, lot.LotId)).GetProperty("needsCompleting").GetBoolean());
    }

    [Fact]
    public async Task A_cage_corrected_or_removed_after_completion_changes_the_lot_only_when_the_weighing_is_completed_again()
    {
        using var lot = await OneLineLotAsync();
        var first = (await AddCageAsync(lot.Owner, lot.LotId, 48.5m)).GetProperty("cages")[0].Id();
        var second = (await AddCageAsync(lot.Owner, lot.LotId, 50m)).GetProperty("cages")[1].Id();
        await AddCageAsync(lot.Owner, lot.LotId, 49m);
        (await CompleteAsync(lot.Owner, lot.LotId)).EnsureSuccessStatusCode();

        var corrected = await (await lot.Owner.PutAsJsonAsync($"/api/harvest-lots/{lot.LotId}/cages/{first}", new { kg = 50.5m })).ReadAsync(HttpStatusCode.OK);
        Assert.True(corrected.GetProperty("needsCompleting").GetBoolean());
        Assert.Equal((149.5m, 147.5m), (corrected.Kg("cagesKg"), corrected.Kg("weighedKg")));
        Assert.Equal(147.5m, (await lot.Owner.GetHarvestLotAsync(lot.LotId)).Kg("weighedKg"));

        var removed = await (await lot.Owner.DeleteAsync($"/api/harvest-lots/{lot.LotId}/cages/{second}")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal([1, 3], Numbers(removed));

        var again = (await (await CompleteAsync(lot.Owner, lot.LotId)).ReadAsync(HttpStatusCode.OK)).GetProperty("lot");
        Assert.Equal(99.5m, again.Kg("weighedKg"));
        // A removed cage leaves its number unused.
        Assert.Equal([1, 3, 4], Numbers(await AddCageAsync(lot.Owner, lot.LotId, 1m)));
    }

    [Fact]
    public async Task Completing_a_lot_with_no_cage_names_the_cages_and_leaves_it_awaiting_its_weighing()
    {
        using var lot = await OneLineLotAsync();

        Assert.Equal(["Cages", "Line 1 cages"], await (await CompleteAsync(lot.Owner, lot.LotId)).InvalidFieldsAsync());
        Assert.Equal(StatusIds.HarvestLot.AwaitingWeighing, (await lot.Owner.GetHarvestLotAsync(lot.LotId)).GetProperty("statusId").GetInt32());
    }

    [Fact]
    public async Task A_cage_needs_kg_above_zero_with_at_most_three_decimals_and_a_product_of_the_lot()
    {
        using var lot = await OneLineLotAsync();
        var lotId = lot.LotId.ToString();

        Assert.Equal(["Kg"], await (await lot.Owner.PostCageAsync(lotId, null, 0m)).InvalidFieldsAsync());
        Assert.Equal(["Kg"], await (await lot.Owner.PostCageAsync(lotId, null, 1.2345m)).InvalidFieldsAsync());
        Assert.Equal(["Product"], await (await lot.Owner.PostCageAsync(lotId, Guid.NewGuid().ToString(), 10m)).InvalidFieldsAsync());
        Assert.Empty((await SheetAsync(lot.Owner, lot.LotId)).GetProperty("cages").EnumerateArray());
    }

    [Fact]
    public async Task A_lot_with_no_product_line_weighs_cages_without_a_product()
    {
        using var api = new AuthApiFactory();
        var raft = await api.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var lot = await owner.RecordLotAsync(raft.RaftId, HarvestFields.OfLanes(1));
        Assert.Empty(lot.GetProperty("productLines").EnumerateArray());

        Assert.Equal(["Product"], await (await owner.PostCageAsync(lot.Id().ToString(), (await SalesKit.CreateProductAsync(owner)).Id().ToString(), 10m)).InvalidFieldsAsync());
        await AddCageAsync(owner, lot.Id(), 20m);
        await AddCageAsync(owner, lot.Id(), 22.5m);

        Assert.Equal(42.5m, (await (await CompleteAsync(owner, lot.Id())).ReadAsync(HttpStatusCode.OK)).GetProperty("lot").Kg("weighedKg"));
    }

    [Fact]
    public async Task A_cage_sent_again_with_its_client_operation_id_is_weighed_once()
    {
        using var lot = await OneLineLotAsync();
        var operation = Guid.NewGuid();

        (await lot.Owner.PostCageAsync(lot.LotId.ToString(), null, 48.5m, operation)).EnsureSuccessStatusCode();
        var again = await (await lot.Owner.PostCageAsync(lot.LotId.ToString(), null, 48.5m, operation)).ReadAsync(HttpStatusCode.OK);

        Assert.Single(again.GetProperty("cages").EnumerateArray());
    }

    [Fact]
    public async Task A_closed_lot_takes_no_cage()
    {
        using var lot = await OneLineLotAsync();
        await lot.Api.UpdateAsync<HarvestLot>(lot.LotId, l =>
        {
            l.StatusId = StatusIds.HarvestLot.Closed;
            l.CloseDate = FarmingTestKit.Today;
            l.LeftoverKg = 0m;
        });

        var response = await lot.Owner.PostCageAsync(lot.LotId.ToString(), null, 48.5m);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.False((await SheetAsync(lot.Owner, lot.LotId)).GetProperty("canEdit").GetBoolean());
    }
}
