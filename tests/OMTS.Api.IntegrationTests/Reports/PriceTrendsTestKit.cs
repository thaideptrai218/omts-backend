using System.Net;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Sales;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceChangeTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesReturnTestKit;

namespace OMTS.Api.IntegrationTests.Reports;

/// <summary>REPORT-02 calls, and the sales the tests need that REPORT-01's kit does not have (an invoice cancelled, an invoice returned and the return cancelled). Names differ from the REPORT-01 kit's, which share this namespace.</summary>
public static class PriceTrendsTestKit
{
    private const string Trends = "/api/reports/price-trends";

    public static Task<HttpResponseMessage> GetPriceTrendsAsync(this HttpClient client, string query = "") => client.GetAsync($"{Trends}?{query}");

    public static async Task<JsonElement> PriceTrendsAsync(this HttpClient client, string query = "") => await (await client.GetPriceTrendsAsync(query)).ReadAsync(HttpStatusCode.OK);

    public static Task<HttpResponseMessage> GetPriceTrendLinesAsync(this HttpClient client, string query) => client.GetAsync($"{Trends}/lines?{query}");

    public static Task<HttpResponseMessage> GetPriceTrendsExportAsync(this HttpClient client, string query = "") => client.GetAsync($"{Trends}/export?{query}");

    public static List<JsonElement> TrendSeries(this JsonElement report) => report.GetProperty("series").EnumerateArray().ToList();

    public static JsonElement TrendSeriesOf(this JsonElement report, Guid productId) => report.TrendSeries().Single(s => s.GetProperty("productId").GetGuid() == productId);

    public static List<JsonElement> TrendPeriods(this JsonElement series) => series.GetProperty("periods").EnumerateArray().ToList();

    public static List<JsonElement> TrendLines(this JsonElement list) => list.GetProperty("lines").EnumerateArray().ToList();

    /// <summary>The soldKg, averagePrice, lowestPrice and highestPrice of a period of the report.</summary>
    public static (decimal SoldKg, decimal Average, decimal Lowest, decimal Highest) PriceFigures(this JsonElement period) =>
        (period.Money("soldKg"), period.Money("averagePrice"), period.Money("lowestPrice"), period.Money("highestPrice"));

    /// <summary>Cancels the invoice (SALE-08) with the Owner's reason.</summary>
    public static async Task CancelSaleAsync(this SaleSetup sale, Guid invoiceId) =>
        await sale.Owner.Client.CancelledAsync(invoiceId, Cancellation(await sale.Owner.Client.InvoiceAsync(invoiceId)));

    /// <summary>A return of the whole invoice and then its cancellation (CLAIM-03.3): the invoice has a return that does not stand. The id of the cancelled return.</summary>
    public static async Task<Guid> ReturnAndCancelAsync(this SaleSetup sale, Guid invoiceId)
    {
        var owner = sale.Owner.Client;
        var returned = (await owner.ReturnedAsync(invoiceId, ReturnBody(await owner.InvoiceAsync(invoiceId), "BackForSale"))).GetProperty("return");
        await (await owner.CancelSalesReturnAsync(returned.Id(), CancelBody(returned))).ReadAsync(HttpStatusCode.OK);
        return returned.Id();
    }
}
