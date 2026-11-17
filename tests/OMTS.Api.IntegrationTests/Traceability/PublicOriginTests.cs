using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using static OMTS.Api.IntegrationTests.Sales.PurchaseRequestTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceSendingTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;

namespace OMTS.Api.IntegrationTests.Traceability;

/// <summary>
/// The public provenance page a QR code opens (BR-ORIGIN-01, BR-ORIGIN-05): anyone reads it without a login; it shows the origin of the published lots and never the buyer, the
/// staff or a price (decided 10/11/2026); a withdrawn lot shows only as withdrawn.
/// </summary>
public class PublicOriginTests
{
    private static string TokenOf(JsonElement finished) => finished.Text("qrUrl").Split("/origin/")[1];

    [Fact]
    public async Task Anyone_reads_the_origin_of_the_published_lot_without_the_buyer_or_prices_and_a_withdrawal_hides_it()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var invoice = await sale.Manager.Client.SoldAsync(sale, sale.LotId, kg: 12.5m);
        var token = TokenOf(await sale.Manager.Client.FinishedAsync(invoice.Id(), wantsQrCode: true));
        var visitor = api.CreateClient();

        var response = await visitor.GetAsync($"/api/origin/{token}");
        var body = await response.Content.ReadAsStringAsync();
        var page = await response.ReadAsync(HttpStatusCode.OK);

        Assert.False(string.IsNullOrEmpty(page.Text("cooperativeName")));
        var lot = Assert.Single(page.GetProperty("lots").EnumerateArray());
        Assert.Equal(("Published", JsonValueKind.Null), (lot.Text("originState"), lot.GetProperty("withdrawnAt").ValueKind));
        var detail = lot.GetProperty("detail");
        Assert.Equal((12.5m, 1), (detail.Money("soldKg"), detail.GetProperty("laneCount").GetInt32()));
        Assert.Equal(invoice.Text("invoiceDate"), detail.Text("soldOn"));
        Assert.Single(detail.GetProperty("productNames").EnumerateArray());
        Assert.NotEmpty(detail.GetProperty("seedLots").EnumerateArray());
        Assert.Equal(JsonValueKind.String, detail.GetProperty("firstSeedingDate").ValueKind);

        // Nothing about the buyer, the staff or the money.
        Assert.DoesNotContain(invoice.GetProperty("customer").Text("name"), body);
        Assert.DoesNotContain("price", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("amount", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(invoice.Text("code"), body);

        // BR-TRACE-06: a withdrawn lot shows its withdrawal, not its origin.
        await api.WithdrawAsync(sale.LotId);
        var withdrawn = Assert.Single((await (await visitor.GetAsync($"/api/origin/{token}")).ReadAsync(HttpStatusCode.OK)).GetProperty("lots").EnumerateArray());
        Assert.Equal(("Withdrawn", JsonValueKind.String, JsonValueKind.Null),
            (withdrawn.Text("originState"), withdrawn.GetProperty("withdrawnAt").ValueKind, withdrawn.GetProperty("detail").ValueKind));
    }

    [Fact]
    public async Task An_unknown_token_or_an_invoice_without_a_published_lot_answers_404()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var invoice = await sale.Manager.Client.SoldAsync(sale, sale.LotId);
        var token = TokenOf(await sale.Manager.Client.FinishedAsync(invoice.Id(), wantsQrCode: true));
        var visitor = api.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync("/api/origin/not-a-token")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync($"/api/origin/{new string('a', 65)}")).StatusCode);

        // A lot published and then never again: only the publish makes the page.
        await api.UpdateAsync<OMTS.Domain.Entities.Harvest.HarvestLot>(sale.LotId, l => (l.PublishedAt, l.WithdrawnAt) = (null, null));
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync($"/api/origin/{token}")).StatusCode);
    }
}
