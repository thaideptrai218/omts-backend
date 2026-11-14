using System.Net;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Sales;
using OMTS.Domain.Entities.Sales;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceChangeTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesReturnTestKit;

namespace OMTS.Api.IntegrationTests.Reports;

/// <summary>REPORT-01 calls and the sales the tests need: invoices of known amounts on the debt, a walk-in sale, an invoice moved to another date, a return after a price cut.</summary>
public static class SalesPerformanceTestKit
{
    private const string Report = "/api/reports/sales-performance";

    public static Task<HttpResponseMessage> GetReportAsync(this HttpClient client, string query = "") => client.GetAsync($"{Report}?{query}");

    public static async Task<JsonElement> ReportAsync(this HttpClient client, string query = "") => await (await client.GetReportAsync(query)).ReadAsync(HttpStatusCode.OK);

    public static Task<HttpResponseMessage> GetGroupInvoicesAsync(this HttpClient client, string query) => client.GetAsync($"{Report}/invoices?{query}");

    public static Task<HttpResponseMessage> GetExportAsync(this HttpClient client, string query = "") => client.GetAsync($"{Report}/export?{query}");

    public static List<JsonElement> Groups(this JsonElement report) => report.GetProperty("groups").EnumerateArray().ToList();

    public static JsonElement Group(this JsonElement report, string key) => report.Groups().Single(g => g.Text("key") == key);

    public static JsonElement Totals(this JsonElement report) => report.GetProperty("totals");

    /// <summary>The sum of a figure over the groups of the report: it must be the total.</summary>
    public static decimal SumOf(this JsonElement report, string figure) => report.Groups().Sum(g => g.Money(figure));

    public static string Day(DateOnly date) => date.ToString("yyyy-MM-dd");

    public static string Period(DateOnly from, DateOnly to) => $"from={Day(from)}&to={Day(to)}";

    /// <summary>One line of <paramref name="kg"/> kg of the setup's product (30,000 VND/kg unless <paramref name="unitPrice"/>), sold by the Owner; the lot stays open.</summary>
    public static async Task<JsonElement> SellKgAsync(
        this SaleSetup sale, Guid? customerId, decimal kg, Guid? lotId = null, decimal? unitPrice = null, decimal discount = 0m, decimal otherCharges = 0m)
    {
        var lot = lotId ?? sale.LotId;
        var total = Math.Round(kg * (unitPrice ?? ListPrice)) - discount + otherCharges;
        // A customer buys on the debt; a walk-in customer pays the whole total in cash (BR-SALE-07).
        var payment = customerId is null ? Cash(total) : Cash(0m, "PayLater", payLaterOwnerAgreed: true);
        var body = Invoice(customerId, [Line(sale.ProductId, lot, kg, unitPrice, priceDecidedByOwner: unitPrice is not null)], payment, lot)
            .With("discount", discount == 0m ? null : discount)
            .With("discountDecidedByOwner", discount != 0m)
            .With("otherCharges", otherCharges == 0m ? null : otherCharges);
        return (await sale.Owner.Client.SellAsync(body)).GetProperty("invoice");
    }

    /// <summary>A price cut of SALE-07 on the invoice (its row version is read first).</summary>
    public static async Task CutPriceAsync(this SaleSetup sale, Guid invoiceId, decimal amount)
    {
        var invoice = await sale.Owner.Client.InvoiceAsync(invoiceId);
        await sale.Owner.Client.CorrectedAsync(invoiceId, PriceCut(invoice, amount));
    }

    /// <summary>A sales return of the whole invoice, every line back for sale (CLAIM-03); the id of the return.</summary>
    public static async Task<Guid> ReturnAsync(this SaleSetup sale, Guid invoiceId)
    {
        var invoice = await sale.Owner.Client.InvoiceAsync(invoiceId);
        return (await sale.Owner.Client.ReturnedAsync(invoiceId, ReturnBody(invoice, "BackForSale"))).GetProperty("return").Id();
    }

    /// <summary>Moves the invoice to a business date (UTC+7), as its invoice time of 12:00 on that date would.</summary>
    public static Task DateAsync(this AuthApiFactory api, Guid invoiceId, DateOnly date) =>
        api.UpdateAsync<SalesInvoice>(invoiceId, i => (i.InvoiceDate, i.InvoicedAt) = (date, new DateTimeOffset(date.ToDateTime(new TimeOnly(12, 0)), TimeSpan.FromHours(7))));

    /// <summary>A string property; declared here so the Reports tests do not hit the same helper of the Farming and Sales kits.</summary>
    public static string Text(this JsonElement element, string property) => element.GetProperty(property).GetString()!;

    /// <summary>The fields named by a 400 MSG-GEN-01 (the Sales kit's helper, named here for the same reason).</summary>
    public static Task<List<string>> InvalidFieldsAsync(this HttpResponseMessage response) => PurchaseRequestTestKit.InvalidFieldsAsync(response);
}
