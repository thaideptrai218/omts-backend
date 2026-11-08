using System.Net;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>SALE-09 calls and what the answers hold.</summary>
public static class SalesInvoiceSearchTestKit
{
    public static Task<HttpResponseMessage> GetInvoicesAsync(this HttpClient client, string query = "") => client.GetAsync("/api/sales-invoices" + query);

    public static async Task<JsonElement> InvoiceListAsync(this HttpClient client, string query = "") => await (await client.GetInvoicesAsync(query)).ReadAsync(HttpStatusCode.OK);

    public static Task<HttpResponseMessage> GetInvoiceAsync(this HttpClient client, Guid id) => client.GetAsync($"/api/sales-invoices/{id}");

    public static async Task<JsonElement> InvoiceAsync(this HttpClient client, Guid id) => await (await client.GetInvoiceAsync(id)).ReadAsync(HttpStatusCode.OK);

    public static Task<HttpResponseMessage> ExportInvoicesAsync(this HttpClient client, string query = "") => client.GetAsync("/api/sales-invoices/export" + query);

    public static Task<HttpResponseMessage> PrintInvoiceAsync(this HttpClient client, Guid id) => client.GetAsync($"/api/sales-invoices/{id}/print");

    public static List<string> Codes(this JsonElement list) => list.Rows().Select(r => r.Text("code")!).ToList();

    public static List<string> Actions(this JsonElement invoice) => invoice.GetProperty("allowedActions").EnumerateArray().Select(a => a.GetString()!).ToList();

    /// <summary>The invoice sold and put on the debt for the rest; the lot stays open.</summary>
    public static object PayLater(this SaleSetup sale, Guid lotId, decimal kg, decimal cash = 0m, Guid? customerId = null) =>
        Invoice(customerId ?? sale.CustomerId, [Line(sale.ProductId, lotId, kg)], Cash(cash, "PayLater", payLaterOwnerAgreed: true), lotId);

    public static string Query(params (string Name, object? Value)[] parameters) =>
        "?" + string.Join('&', parameters.Where(p => p.Value is not null).Select(p => $"{p.Name}={Uri.EscapeDataString(p.Value is DateOnly d ? d.ToString("yyyy-MM-dd") : p.Value!.ToString()!)}"));
}
