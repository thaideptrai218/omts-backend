using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Harvest;
using OMTS.Api.IntegrationTests.Infrastructure;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>One pass through the API: the request in cages, the weighing of each cage both sides confirm, the invoice made from it, and the invoice the trader reads.</summary>
public class OrderToInvoiceFlowTests
{
    private static Task<JsonElement> OpenAsync(HttpClient client, Guid customerId) =>
        client.GetFromJsonAsync<JsonElement>($"/api/sales-invoices/open-purchase-requests?customerId={customerId}");

    [Fact]
    public async Task A_request_in_cages_is_weighed_confirmed_and_invoiced_and_the_trader_reads_it()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var trader = await api.TraderAsync();
        var client = sale.Manager.Client;

        var request = await trader.Client.PlaceAsync(sale.ProductId, 5);
        await (await sale.Owner.Client.AcceptAsync(request, payLater: true)).ReadAsync(HttpStatusCode.OK);
        var offered = Assert.Single((await OpenAsync(client, trader.Profile.Id)).EnumerateArray());
        Assert.Equal((5m, 0m), (offered.Money("acceptedCages"), offered.Money("deliveredCages")));

        // Two lots on the raft for this request, then the Owner marks its harvest done (decision of 11/11/2026).
        sale = await sale.HarvestedForAsync(request);
        var harvest = (await sale.Owner.Client.RequestDetailAsync(request.Id())).GetProperty("harvest");
        Assert.Equal((16, 2), (harvest.GetProperty("harvestedCages").GetInt32(), harvest.GetProperty("lots").GetArrayLength()));
        Assert.NotEqual(JsonValueKind.Null, harvest.GetProperty("completedAt").ValueKind);

        // Two cages first: the request stays open and counts them once they are invoiced.
        var firstWeighing = await client.ConfirmedWeighingAsync(trader.Client, request.Id(), PortWeighingTestKit.WeighedLot(sale.LotId, 20m, 22.5m));
        Assert.Equal(0m, Assert.Single((await OpenAsync(client, trader.Profile.Id)).EnumerateArray()).Money("deliveredCages"));
        await client.SellAsync(Invoice(trader.Profile.Id, [PortWeighingTestKit.WeighedLine(sale.ProductId, sale.LotId)], Cash(0m, "PayLater"), sale.LotId)
            .FromWeighing(firstWeighing).With("takesRequestInFull", false));
        Assert.Equal(2m, Assert.Single((await OpenAsync(client, trader.Profile.Id)).EnumerateArray()).Money("deliveredCages"));

        // The last three cages take the request in full, and the end of the sale sends the invoice.
        var lastWeighing = await client.ConfirmedWeighingAsync(trader.Client, request.Id(), PortWeighingTestKit.WeighedLot(sale.OtherLotId, 10m, 10m, 10m));
        var last = (await client.SellAsync(Invoice(trader.Profile.Id, [PortWeighingTestKit.WeighedLine(sale.ProductId, sale.OtherLotId)], Cash(900_000m))
            .FromWeighing(lastWeighing))).GetProperty("invoice");
        Assert.Empty((await OpenAsync(client, trader.Profile.Id)).EnumerateArray());
        Assert.True((await trader.Client.MineAsync(request.Id())).GetProperty("fulfilled").GetBoolean());

        // A request taken in full before lots named their request has no harvest mark: it is still never offered for a lot (decision of 11/11/2026).
        await api.UpdateAsync<OMTS.Domain.Entities.Sales.PurchaseRequest>(request.Id(), r => (r.HarvestCompletedAt, r.HarvestCompletedByAccountId) = (null, null));
        Assert.DoesNotContain(request.Id(), (await sale.Owner.Client.HarvestScreenAsync(sale.RaftId)).GetProperty("orders").EnumerateArray().Select(o => o.Id()));
        Assert.Equal("MSG-GEN-09", await (await sale.Owner.Client.PostHarvestAsync(sale.RaftId, new HarvestFields().For(request.Id()))).ErrorCodeAsync());
        await (await client.PostAsJsonAsync($"/api/sales-invoices/{last.Id()}/finish", new { wantsQrCode = false })).ReadAsync(HttpStatusCode.OK);

        var list = await trader.Client.GetFromJsonAsync<JsonElement>("/api/me/invoices");
        Assert.Equal(JsonValueKind.Null, list.GetProperty("message").ValueKind);
        Assert.Equal(2, list.GetProperty("page").GetProperty("total").GetInt32());
        var rows = list.GetProperty("page").GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(last.Id(), rows[0].Id());
        Assert.Equal((3, 30m, 900_000m), (rows[0].GetProperty("cageCount").GetInt32(), rows[0].Money("totalKg"), rows[0].Money("total")));
        Assert.Equal((2, 42.5m, 1_275_000m), (rows[1].GetProperty("cageCount").GetInt32(), rows[1].Money("totalKg"), rows[1].Money("total")));
        Assert.Equal(request.Text("code"), rows[0].Text("purchaseRequestCode"));
        Assert.Equal(1_275_000m, rows.Sum(r => r.Money("unpaidAmount")));

        var detail = await trader.Client.GetFromJsonAsync<JsonElement>($"/api/me/invoices/{last.Id()}");
        var line = Assert.Single(detail.GetProperty("lines").EnumerateArray());
        Assert.Equal([10m, 10m, 10m], line.GetProperty("cageKgs").EnumerateArray().Select(k => k.GetDecimal()));
        Assert.Equal((30m, 30_000m, 900_000m), (line.Money("kg"), line.Money("unitPrice"), line.Money("amount")));
        Assert.Equal((3, lastWeighing.Id()), (detail.GetProperty("cageCount").GetInt32(), detail.GetProperty("portWeighingId").GetGuid()));
        Assert.Equal(900_000m, Assert.Single(detail.GetProperty("payments").EnumerateArray()).Money("amount"));

        // Staff data stays with the staff.
        Assert.False(line.TryGetProperty("listPrice", out _));
        Assert.False(detail.TryGetProperty("sendings", out _));
        Assert.False(detail.TryGetProperty("createdBy", out _));
        Assert.False(detail.TryGetProperty("allowedActions", out _));
    }

    [Fact]
    public async Task A_trader_reads_only_their_own_invoices_and_staff_do_not_use_these_routes()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var buyer = await api.TraderAsync();
        var other = await api.TraderAsync();
        var invoice = (await sale.Manager.Client.SellAsync(Invoice(buyer.Profile.Id, [Line(sale.ProductId, sale.LotId, 10m)], Cash(300_000m)))).GetProperty("invoice");

        Assert.Equal(HttpStatusCode.OK, (await buyer.Client.GetAsync($"/api/me/invoices/{invoice.Id()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Client.GetAsync($"/api/me/invoices/{invoice.Id()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await buyer.Client.GetAsync($"/api/me/invoices/{Guid.NewGuid()}")).StatusCode);

        var empty = await other.Client.GetFromJsonAsync<JsonElement>("/api/me/invoices");
        Assert.Empty(empty.GetProperty("page").GetProperty("items").EnumerateArray());
        Assert.Equal("MSG-GEN-11", empty.Text("messageCode"));

        Assert.Equal("MSG-GEN-08", await (await sale.Manager.Client.GetAsync("/api/me/invoices")).ErrorCodeAsync());
        Assert.Equal("MSG-GEN-08", await (await sale.Owner.Client.GetAsync($"/api/me/invoices/{invoice.Id()}")).ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.CreateClient().GetAsync("/api/me/invoices")).StatusCode);
    }
}
