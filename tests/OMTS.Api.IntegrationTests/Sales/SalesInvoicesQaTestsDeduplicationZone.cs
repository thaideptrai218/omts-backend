using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>QA of PR #58, F1: the deduplication key of SALE-05 never shows an invoice of a zone the caller cannot view (BR-SALE-08, GFR-02).</summary>
public class SalesInvoicesQaTestsDeduplicationZone
{
    // F1: a Zone Manager of zone 2 sends the key of a zone 1 sale. The answer is the one GET gives for that invoice (403 MSG-GEN-08), not its view with the customer and payments.
    [Fact]
    public async Task F1_a_deduplication_key_of_an_invoice_in_another_zone_answers_msg_gen_08_and_shows_nothing_of_it()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var otherZone = (await sale.Owner.Client.CreateZoneAsync()).Id();
        var outsider = await api.ManagerAsync(otherZone);
        var body = Invoice(sale.CustomerId, [Line(sale.ProductId, sale.LotId, 10m)], Cash(300_000m), sale.LotId).With("deduplicationKey", "tap-zone-1");
        var first = (await sale.Manager.Client.SellAsync(body)).GetProperty("invoice");
        var customerName = first.GetProperty("customer").Text("name");

        var answer = await outsider.Client.PostInvoiceAsync(body);

        var text = await answer.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Forbidden, answer.StatusCode);
        Assert.Equal("MSG-GEN-08", JsonDocument.Parse(text).RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain(first.Text("code")!, text);
        Assert.DoesNotContain(customerName!, text);
        Assert.DoesNotContain(first.Id().ToString(), text);

        // The same as GET of that invoice answers this Zone Manager.
        var read = await outsider.Client.GetAsync($"/api/sales-invoices/{first.Id()}");
        Assert.Equal((HttpStatusCode.Forbidden, "MSG-GEN-08"), (read.StatusCode, await read.ErrorCodeAsync()));

        // The Zone Manager of the zone still gets the first invoice back, and nothing was sold twice.
        Assert.Equal(first.Id(), (await sale.Manager.Client.SellAsync(body)).GetProperty("invoice").Id());
        Assert.Equal((1, 1), (await api.QueryAsync(db => db.SalesInvoices.CountAsync()), await api.QueryAsync(db => db.CustomerPayments.CountAsync())));
    }
}
