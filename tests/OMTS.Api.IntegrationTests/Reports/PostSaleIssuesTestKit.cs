using System.Net;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Sales;
using OMTS.Domain.Entities.Sales;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;

namespace OMTS.Api.IntegrationTests.Reports;

/// <summary>REPORT-03 calls, and the dates the tests move the events to (an event is counted by its own date, BR-REPORT-03). Names differ from the other report kits', which share this namespace.</summary>
public static class PostSaleIssuesTestKit
{
    private const string PostSale = "/api/reports/post-sale-issues";

    public static Task<HttpResponseMessage> GetPostSaleAsync(this HttpClient client, string query = "") => client.GetAsync($"{PostSale}?{query}");

    public static async Task<JsonElement> PostSaleAsync(this HttpClient client, string query = "") => await (await client.GetPostSaleAsync(query)).ReadAsync(HttpStatusCode.OK);

    public static Task<HttpResponseMessage> GetPostSaleItemsAsync(this HttpClient client, string query) => client.GetAsync($"{PostSale}/items?{query}");

    public static Task<HttpResponseMessage> GetPostSaleExportAsync(this HttpClient client, string query = "") => client.GetAsync($"{PostSale}/export?{query}");

    public static List<JsonElement> PostSaleItems(this JsonElement list) => list.GetProperty("items").EnumerateArray().ToList();

    /// <summary>The claims, returns, price cuts and cancelled invoices of a group as (claimsSent, returnsCount, priceCutsCount, cancelledCount).</summary>
    public static (int Claims, int Returns, int PriceCuts, int Cancelled) EventCounts(this JsonElement group) =>
        (group.GetProperty("claimsSent").GetInt32(), group.GetProperty("returnsCount").GetInt32(), group.GetProperty("priceCutsCount").GetInt32(), group.GetProperty("cancelledCount").GetInt32());

    /// <summary>The instant at 12:00 Vietnam time (UTC+7) of a business date.</summary>
    public static DateTimeOffset NoonOf(DateOnly date) => new(date.ToDateTime(new TimeOnly(12, 0)), TimeSpan.FromHours(7));

    /// <summary>The claim of the trader on the invoice, sent now; its id.</summary>
    public static async Task<Guid> SendClaimAsync(this TraderActor trader, Guid invoiceId) => (await trader.Client.SubmitClaimAsync(invoiceId)).Id();

    public static Task MoveClaimAsync(this AuthApiFactory api, Guid claimId, DateTimeOffset sentAt, int? statusId = null) =>
        api.UpdateAsync<QualityClaim>(claimId, c => (c.SentAt, c.StatusId) = (sentAt, statusId ?? c.StatusId));

    public static Task MoveReturnAsync(this AuthApiFactory api, Guid returnId, DateOnly date) => api.UpdateAsync<SalesReturn>(returnId, r => r.ReturnDate = date);

    public static Task MoveCancellationAsync(this AuthApiFactory api, Guid invoiceId, DateTimeOffset at) => api.UpdateAsync<SalesInvoice>(invoiceId, i => i.CancelledAt = at);

    /// <summary>Moves every price cut of the invoice to a date.</summary>
    public static Task MovePriceCutsAsync(this AuthApiFactory api, Guid invoiceId, DateOnly date) => api.UseDbAsync(db =>
    {
        foreach (var cut in db.SalesInvoicePriceCuts.Where(p => p.InvoiceId == invoiceId).ToList())
        {
            cut.CutDate = date;
        }
    });

    /// <summary>One invoice of two lots (6 kg and 4 kg of the setup's product, on the debt) for a customer; the lots stay open.</summary>
    public static async Task<JsonElement> SellTwoLotsAsync(this SaleSetup sale, Guid customerId)
    {
        var body = Invoice(
            customerId, [Line(sale.ProductId, sale.LotId, 6m), Line(sale.ProductId, sale.OtherLotId, 4m)], Cash(0m, "PayLater", payLaterOwnerAgreed: true), sale.LotId, sale.OtherLotId);
        return (await sale.Owner.Client.SellAsync(body)).GetProperty("invoice");
    }
}
