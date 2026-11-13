using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Harvest;
using OMTS.Api.IntegrationTests.Infrastructure;
using static OMTS.Api.IntegrationTests.Sales.PortWeighingTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>Decision of 09/11/2026: after the harvest each cage is weighed at the port, both sides close the figures, and the invoice is made from that weighing.</summary>
public class PortWeighingTests
{
    private static async Task<(AuthApiFactory Api, SaleSetup Sale, TraderActor Trader, JsonElement Request)> AcceptedAsync(int cages = 5)
    {
        var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var trader = await api.TraderAsync();
        var request = await trader.Client.PlaceAsync(sale.ProductId, cages);
        await (await sale.Owner.Client.AcceptAsync(request)).ReadAsync(HttpStatusCode.OK);
        return (api, await sale.HarvestedForAsync(request), trader, request);
    }

    private static decimal[] Kgs(JsonElement lot) => lot.GetProperty("cageKgs").EnumerateArray().Select(k => k.GetDecimal()).ToArray();

    [Fact]
    public async Task Each_cage_is_weighed_and_the_weighing_adds_them_up_by_lot()
    {
        var (api, sale, _, request) = await AcceptedAsync();
        using (api)
        {
            var weighing = await sale.Manager.Client.WeighAsync(request.Id(), WeighedLot(sale.LotId, 10.5m, 9.25m), WeighedLot(sale.OtherLotId, 11m));

            Assert.Equal((3, 30.75m), (weighing.GetProperty("cageCount").GetInt32(), weighing.Money("totalKg")));
            Assert.Equal(JsonValueKind.Null, weighing.GetProperty("confirmedAt").ValueKind);
            var lots = weighing.GetProperty("lots").EnumerateArray().ToList();
            Assert.Equal((sale.LotId, 19.75m), (lots[0].GetProperty("harvestLotId").GetGuid(), lots[0].Money("kg")));
            Assert.Equal([10.5m, 9.25m], Kgs(lots[0]));
            Assert.Equal([1, 2, 3], await api.QueryAsync(db => db.PortWeighingCages.OrderBy(c => c.CageNumber).Select(c => c.CageNumber).ToListAsync()));
        }
    }

    [Fact]
    public async Task The_kg_of_each_cage_and_the_lots_are_checked()
    {
        var (api, sale, _, request) = await AcceptedAsync();
        using (api)
        {
            var client = sale.Manager.Client;
            object Lot(params decimal?[] cageKgs) => new { harvestLotId = sale.LotId, cageKgs };

            Assert.Equal(["Lot 1 cage kg"], await (await client.PostWeighingAsync(request.Id(), Lot(10m, 0m))).InvalidFieldsAsync());
            Assert.Equal(["Lot 1 cage kg"], await (await client.PostWeighingAsync(request.Id(), Lot(10m, null))).InvalidFieldsAsync());
            Assert.Equal(["Lot 1 cage kg"], await (await client.PostWeighingAsync(request.Id(), Lot(1.2345m))).InvalidFieldsAsync());
            Assert.Equal(["Lot 1 cage kg"], await (await client.PostWeighingAsync(request.Id(), Lot())).InvalidFieldsAsync());
            Assert.Equal(["Lots"], await (await client.PostWeighingAsync(request.Id())).InvalidFieldsAsync());
            Assert.Equal(["Lot 2 harvest lot"], await (await client.PostWeighingAsync(request.Id(), Lot(1m), Lot(2m))).InvalidFieldsAsync());
            Assert.Equal(["Lot 1 harvest lot"], await (await client.PostWeighingAsync(request.Id(), WeighedLot(Guid.NewGuid(), 1m))).InvalidFieldsAsync());
            Assert.Equal(0, await api.QueryAsync(db => db.PortWeighings.CountAsync()));
        }
    }

    [Fact]
    public async Task Only_an_accepted_request_is_weighed_and_one_weighing_waits_for_the_trader_at_a_time()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var trader = await api.TraderAsync();
        var waiting = await trader.Client.PlaceAsync(sale.ProductId, 2);
        Assert.Equal("MSG-GEN-09", await (await sale.Manager.Client.PostWeighingAsync(waiting.Id(), WeighedLot(sale.LotId, 1m))).ErrorCodeAsync());

        await (await sale.Owner.Client.AcceptAsync(waiting)).ReadAsync(HttpStatusCode.OK);
        sale = await sale.HarvestedForAsync(waiting);
        var first = await sale.Manager.Client.WeighAsync(waiting.Id(), WeighedLot(sale.LotId, 1m));
        Assert.Equal("MSG-GEN-09", await (await sale.Manager.Client.PostWeighingAsync(waiting.Id(), WeighedLot(sale.LotId, 2m))).ErrorCodeAsync());

        await trader.Client.ConfirmWeighingAsync(first);
        await sale.Manager.Client.WeighAsync(waiting.Id(), WeighedLot(sale.LotId, 2m));
    }

    [Fact]
    public async Task The_figures_are_corrected_until_the_trader_confirms_and_the_trader_confirms_only_what_they_read()
    {
        var (api, sale, trader, request) = await AcceptedAsync();
        using (api)
        {
            var client = sale.Manager.Client;
            var read = await client.WeighAsync(request.Id(), WeighedLot(sale.LotId, 10m, 20m, 30m));

            var corrected = await (await client.PutAsJsonAsync($"/api/port-weighings/{read.Id()}", new { lots = new[] { WeighedLot(sale.OtherLotId, 11m, 21m) }, rowVersion = read.RowVersion() })).ReadAsync(HttpStatusCode.OK);
            Assert.Equal((2, 32m), (corrected.GetProperty("cageCount").GetInt32(), corrected.Money("totalKg")));
            Assert.Equal(sale.OtherLotId, Assert.Single(corrected.GetProperty("lots").EnumerateArray()).GetProperty("harvestLotId").GetGuid());

            // Figures corrected since the trader read them are not confirmed unseen.
            var stale = await trader.Client.PostAsJsonAsync($"/api/me/port-weighings/{read.Id()}/confirm", new { rowVersion = AcsTestKit.StaleRowVersion });
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

            var confirmed = await trader.Client.ConfirmWeighingAsync(corrected);
            Assert.NotEqual(JsonValueKind.Null, confirmed.GetProperty("confirmedAt").ValueKind);
            Assert.Equal("MSG-GEN-09", await (await client.PutAsJsonAsync($"/api/port-weighings/{read.Id()}", new { lots = new[] { WeighedLot(sale.LotId, 1m) }, rowVersion = confirmed.RowVersion() })).ErrorCodeAsync());

            // A second tap changes nothing.
            Assert.Equal(confirmed.GetProperty("confirmedAt").GetDateTimeOffset(), (await trader.Client.ConfirmWeighingAsync(corrected)).GetProperty("confirmedAt").GetDateTimeOffset());
        }
    }

    [Fact]
    public async Task Only_the_trader_of_the_request_confirms_and_staff_do_not_use_the_trader_routes()
    {
        var (api, sale, trader, request) = await AcceptedAsync();
        using (api)
        {
            var other = await api.TraderAsync();
            var weighing = await sale.Manager.Client.WeighAsync(request.Id(), WeighedLot(sale.LotId, 10m));

            Assert.Equal(HttpStatusCode.NotFound, (await other.Client.PostConfirmAsync(weighing)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await other.Client.GetAsync($"/api/me/port-weighings/{weighing.Id()}")).StatusCode);
            Assert.Equal("MSG-GEN-11", (await other.Client.GetFromJsonAsync<JsonElement>("/api/me/port-weighings")).Text("messageCode"));
            Assert.Equal("MSG-GEN-08", await (await sale.Manager.Client.PostConfirmAsync(weighing)).ErrorCodeAsync());
            Assert.Equal("MSG-GEN-08", await (await trader.Client.PostWeighingAsync(request.Id(), WeighedLot(sale.LotId, 1m))).ErrorCodeAsync());
            Assert.Equal(HttpStatusCode.Unauthorized, (await api.CreateClient().PostAsJsonAsync("/api/port-weighings", new { })).StatusCode);

            var mine = await trader.Client.GetFromJsonAsync<JsonElement>("/api/me/port-weighings");
            Assert.Equal(weighing.Id(), Assert.Single(mine.GetProperty("page").GetProperty("items").EnumerateArray()).Id());
            var ofRequest = await sale.Manager.Client.GetFromJsonAsync<JsonElement>($"/api/port-weighings?purchaseRequestId={request.Id()}");
            Assert.Equal(weighing.Id(), Assert.Single(ofRequest.EnumerateArray()).Id());
        }
    }

    [Fact]
    public async Task The_invoice_of_a_request_comes_only_from_a_confirmed_weighing_and_takes_its_kg()
    {
        var (api, sale, trader, request) = await AcceptedAsync();
        using (api)
        {
            var client = sale.Manager.Client;
            var weighing = await client.WeighAsync(request.Id(), WeighedLot(sale.LotId, 10.5m, 9.25m, 11m));
            Dictionary<string, object?> Body(params object[] lines) => Invoice(trader.Profile.Id, lines, Cash(922_500m));

            // Not linked to the weighing, or not confirmed by the trader yet.
            Assert.Equal(["Port weighing"], await (await client.PostInvoiceAsync(Body(Line(sale.ProductId, sale.LotId, 30.75m)).With("purchaseRequestId", request.Id()))).InvalidFieldsAsync());
            Assert.Equal("MSG-GEN-09", await (await client.PostInvoiceAsync(Body(WeighedLine(sale.ProductId, sale.LotId)).FromWeighing(weighing))).ErrorCodeAsync());

            var confirmed = await trader.Client.ConfirmWeighingAsync(weighing);

            // The lines have to be the weighed lots with the weighed kg.
            Assert.Equal(["Line 1 kg"], await (await client.PostInvoiceAsync(Body(Line(sale.ProductId, sale.LotId, 99m)).FromWeighing(confirmed))).InvalidFieldsAsync());
            Assert.Equal(["Line 1 harvest lot"], await (await client.PostInvoiceAsync(Body(WeighedLine(sale.ProductId, sale.OtherLotId)).FromWeighing(confirmed))).InvalidFieldsAsync());
            Assert.Equal(0, await api.QueryAsync(db => db.SalesInvoices.CountAsync()));

            var invoice = (await client.SellAsync(Body(WeighedLine(sale.ProductId, sale.LotId)).FromWeighing(confirmed))).GetProperty("invoice");
            var line = Assert.Single(invoice.GetProperty("lines").EnumerateArray());
            Assert.Equal((30.75m, 922_500m), (line.Money("kg"), line.Money("amount")));
            Assert.Equal([10.5m, 9.25m, 11m], Kgs(line));
            Assert.Equal(confirmed.Id(), invoice.GetProperty("portWeighingId").GetGuid());

            // One invoice per weighing, and the weighing names it.
            Assert.Equal("MSG-GEN-09", await (await client.PostInvoiceAsync(Body(WeighedLine(sale.ProductId, sale.LotId)).FromWeighing(confirmed))).ErrorCodeAsync());
            var read = await client.GetFromJsonAsync<JsonElement>($"/api/port-weighings/{confirmed.Id()}");
            Assert.Equal((invoice.Id(), invoice.Text("code")), (read.GetProperty("invoiceId").GetGuid(), read.Text("invoiceCode")));
        }
    }

    // Decision of 11/11/2026: the Owner marks the harvest done before the weighing, and a request is weighed from its own lots only.
    [Fact]
    public async Task A_request_is_weighed_only_once_its_harvest_is_done_and_only_from_its_own_lots()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var trader = await api.TraderAsync();
        var request = await trader.Client.PlaceAsync(sale.ProductId, 5);
        await (await sale.Owner.Client.AcceptAsync(request)).ReadAsync(HttpStatusCode.OK);
        var own = await sale.Owner.Client.HarvestLotAsync(sale.RaftId, (HarvestFields.OfLanes(3) with { Cages = "5" }).For(request.Id()));

        Assert.Equal("MSG-GEN-09", await (await sale.Manager.Client.PostWeighingAsync(request.Id(), WeighedLot(own.Id(), 10m))).ErrorCodeAsync());

        await sale.Owner.Client.MarkHarvestDoneAsync(request.Id());
        Assert.Equal(["Lot 1 harvest lot"], await (await sale.Manager.Client.PostWeighingAsync(request.Id(), WeighedLot(sale.LotId, 10m))).InvalidFieldsAsync());
        Assert.Equal(0, await api.QueryAsync(db => db.PortWeighings.CountAsync()));
        await sale.Manager.Client.WeighAsync(request.Id(), WeighedLot(own.Id(), 10m));
    }

    [Fact]
    public async Task A_lot_is_not_weighed_past_its_cages_over_the_weighings_of_the_request()
    {
        var (api, sale, trader, request) = await AcceptedAsync();
        using (api)
        {
            var firstLot = (await sale.Owner.Client.RequestDetailAsync(request.Id())).GetProperty("harvest").GetProperty("lots")[0];
            Assert.Equal((sale.LotId, 8), (firstLot.Id(), firstLot.GetProperty("cageCount").GetInt32()));
            var client = sale.Manager.Client;
            var nine = Enumerable.Repeat(1m, 9).ToArray();
            Assert.Equal(["Lot 1 cage kg"], await (await client.PostWeighingAsync(request.Id(), WeighedLot(sale.LotId, nine))).InvalidFieldsAsync());

            // A correction replaces its own cages, so it may take the lot up to its cages.
            var first = await client.WeighAsync(request.Id(), WeighedLot(sale.LotId, 1m));
            var corrected = await (await client.PutAsJsonAsync($"/api/port-weighings/{first.Id()}", new { lots = new[] { WeighedLot(sale.LotId, nine[1..]) }, rowVersion = first.RowVersion() }))
                .ReadAsync(HttpStatusCode.OK);
            await trader.Client.ConfirmWeighingAsync(corrected);

            Assert.Equal(["Lot 1 cage kg"], await (await client.PostWeighingAsync(request.Id(), WeighedLot(sale.LotId, 1m))).InvalidFieldsAsync());
            await client.WeighAsync(request.Id(), WeighedLot(sale.OtherLotId, 1m));
        }
    }

    [Fact]
    public async Task A_sale_without_a_request_still_gives_the_kg_itself()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var byKg = (await sale.Manager.Client.SellAsync(Invoice(sale.CustomerId, [Line(sale.ProductId, sale.LotId, 20m)], Cash(600_000m)))).GetProperty("invoice");
        Assert.Empty(byKg.GetProperty("lines")[0].GetProperty("cageKgs").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, byKg.GetProperty("portWeighingId").ValueKind);
    }
}
