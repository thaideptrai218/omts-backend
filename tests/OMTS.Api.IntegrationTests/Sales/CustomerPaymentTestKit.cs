using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>DEBT-02 calls as the clients send them, and what they leave behind (the Owner's MSG-DEBT-03 rows).</summary>
public static class CustomerPaymentTestKit
{
    private const string Path = "/api/customer-payments";

    /// <summary>A bank reference no other test uses (BR-DEBT-03 warns on a repeated one).</summary>
    public static string NewReference() => "FT-DEMO-" + AcsTestKit.NewCode();

    /// <summary>A body for <see cref="RecordAsync"/> or <see cref="PayBackAsync"/>: another transfer by default, with a fresh bank reference.</summary>
    public static Dictionary<string, object?> MoneyBody(Guid customerId, decimal amount, string method = "OtherTransfer", Guid? collectedBy = null, DateOnly? date = null) => new()
    {
        ["customerId"] = customerId,
        ["paymentDate"] = (date ?? PurchaseRequestTestKit.Today).ToString("yyyy-MM-dd"),
        ["amount"] = amount,
        ["method"] = method,
        ["bankReference"] = method == "OtherTransfer" ? NewReference() : null,
        ["collectedByAccountId"] = collectedBy,
    };

    public static Task<HttpResponseMessage> RecordAsync(this HttpClient client, object body) => client.PostAsJsonAsync(Path, body);

    public static Task<HttpResponseMessage> PayBackAsync(this HttpClient client, object body) => client.PostAsJsonAsync($"{Path}/pay-back", body);

    /// <summary>Records it and returns the whole answer (201).</summary>
    public static async Task<JsonElement> PaidAsync(this HttpClient client, object body) => await (await client.RecordAsync(body)).ReadAsync(HttpStatusCode.Created);

    public static Task<HttpResponseMessage> ConfirmAsync(this HttpClient client, Guid[] confirm, params (Guid Id, string? Reason)[] cancel) =>
        client.PostAsJsonAsync($"{Path}/confirmations", new { confirm, cancel = cancel.Select(c => new { id = c.Id, reason = c.Reason }) });

    public static Task<HttpResponseMessage> CancelAsync(this HttpClient client, Guid paymentId, string? reason) =>
        client.PostAsJsonAsync($"{Path}/{paymentId}/cancel", new { reason });

    public static Task<HttpResponseMessage> GetAwaitingAsync(this HttpClient client) => client.GetAsync($"{Path}/awaiting-confirmation");

    public static Task<HttpResponseMessage> GetFormAsync(this HttpClient client, Guid customerId) => client.GetAsync($"{Path}/form/{customerId}");

    public static Task<HttpResponseMessage> GetPaymentAsync(this HttpClient client, Guid paymentId) => client.GetAsync($"{Path}/{paymentId}");

    public static async Task<List<JsonElement>> AwaitingRowsAsync(this HttpClient client) =>
        (await (await client.GetAwaitingAsync()).ReadAsync(HttpStatusCode.OK)).GetProperty("page").GetProperty("items").EnumerateArray().ToList();

    public static JsonElement Payment(this JsonElement saved) => saved.GetProperty("payment");

    /// <summary>The cash collected at the port with the invoice (BR-SALE-07 (3)), Awaiting confirmation.</summary>
    public static async Task<Guid> PortCashOfAsync(this AuthApiFactory api, JsonElement invoice) => Assert.Single(await api.PaymentsOfAsync(invoice.Id())).Id;

    /// <summary>A Pay later invoice of <paramref name="kg"/> × 30,000 VND with <paramref name="cash"/> at the port.</summary>
    public static async Task<JsonElement> PayLaterAsync(this SaleSetup sale, Guid customerId, decimal kg, decimal cash = 0m) =>
        (await sale.Manager.Client.SellAsync(Invoice(customerId, [Line(sale.ProductId, sale.LotId, kg)], Cash(cash, "PayLater", payLaterOwnerAgreed: true), sale.LotId)))
            .GetProperty("invoice");

    /// <summary>The debt as every screen shows it: the DEBT-03 list (0 when not listed), the statement and the customer view (SALE-04.1).</summary>
    public static async Task<(decimal List, decimal Statement, decimal Customer)> DebtsEverywhereAsync(this HttpClient client, Guid customerId)
    {
        var rows = (await client.DebtListAsync("?pageSize=100")).Rows();
        var listed = rows.Where(r => r.GetProperty("customer").Id() == customerId).Select(r => r.GetProperty("debt").GetDecimal()).SingleOrDefault();
        return (listed, (await client.StatementAsync(customerId)).GetProperty("debtToday").GetDecimal(), await client.CustomerDebtAsync(customerId));
    }

    /// <summary>The Owner's MSG-DEBT-03 rows, oldest first.</summary>
    public static Task<List<(string? Body, string? Params, Guid? RecordId)>> TransactionNoticesAsync(this AuthApiFactory api, Guid ownerId) =>
        api.QueryAsync(async db => (await db.Notifications.AsNoTracking()
                .Where(n => n.Type == NotificationTypes.CustomerPaymentTransaction && n.RecipientAccountId == ownerId)
                .OrderBy(n => n.CreatedAt)
                .Select(n => new { n.Body, n.MessageParams, n.RelatedRecordId, n.MessageCode, n.IsMandatory })
                .ToListAsync())
            .Select(n =>
            {
                Assert.Equal(("MSG-DEBT-03", true), (n.MessageCode, n.IsMandatory));
                return (n.Body, n.MessageParams, n.RelatedRecordId);
            })
            .ToList());

    /// <summary>An Accountant with a pair at All zones, as usually granted.</summary>
    public static Task<Actor> AllZonesAccountantAsync(this AuthApiFactory api) => api.StaffAsync(null, StaffRole.Accountant);
}
