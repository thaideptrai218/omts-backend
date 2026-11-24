using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Harvest;
using OMTS.Api.IntegrationTests.Infrastructure;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>The invoices a trader reads through the API: only their own, and not the staff routes.</summary>
public class OrderToInvoiceFlowTests
{
    [Fact(Skip = "Not run since invoices are made from orders; re-enable once it has been run against SQL Server.")]
    public async Task A_trader_reads_only_their_own_invoices_and_staff_do_not_use_these_routes()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var buyer = await api.TraderAsync();
        var other = await api.TraderAsync();
        var invoice = (await sale.Owner.Client.SellAsync(Invoice(buyer.Profile.Id, [Line(sale.ProductId, sale.LotId, 10m)], Cash(300_000m)))).GetProperty("invoice");

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
