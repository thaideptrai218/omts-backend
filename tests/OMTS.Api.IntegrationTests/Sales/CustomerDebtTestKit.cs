using System.Net;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>
/// DEBT-03 calls, and the rows the tests need before DEBT-02 exists (PR 8b): a payment or money paid back recorded straight in the database, an invoice moved back in time.
/// </summary>
public static class CustomerDebtTestKit
{
    public static Task<HttpResponseMessage> GetDebtsAsync(this HttpClient client, string query = "") => client.GetAsync("/api/customer-debts" + query);

    public static async Task<JsonElement> DebtListAsync(this HttpClient client, string query = "") => await (await client.GetDebtsAsync(query)).ReadAsync(HttpStatusCode.OK);

    public static Task<HttpResponseMessage> GetStatementAsync(this HttpClient client, Guid customerId, DateOnly? from = null, DateOnly? to = null) =>
        client.GetAsync($"/api/customer-debts/{customerId}/statement?{Period(from, to)}");

    public static async Task<JsonElement> StatementAsync(this HttpClient client, Guid customerId, DateOnly? from = null, DateOnly? to = null) =>
        await (await client.GetStatementAsync(customerId, from, to)).ReadAsync(HttpStatusCode.OK);

    public static Task<HttpResponseMessage> ExportDebtsAsync(this HttpClient client, Guid? customerId = null, DateOnly? from = null, DateOnly? to = null) =>
        client.GetAsync($"/api/customer-debts/export?{(customerId is { } id ? $"customerId={id}&" : "")}{Period(from, to)}");

    public static List<JsonElement> Rows(this JsonElement list) => list.GetProperty("page").GetProperty("items").EnumerateArray().ToList();

    public static JsonElement RowOf(this JsonElement list, Guid customerId) => list.Rows().Single(r => r.GetProperty("customer").Id() == customerId);

    public static List<(string? Code, decimal Unpaid)> Unpaid(this JsonElement statement) =>
        statement.GetProperty("unpaidInvoices").EnumerateArray().Select(i => (i.GetProperty("code").GetString(), i.GetProperty("unpaid").GetDecimal())).ToList();

    /// <summary>A payment (or money paid back) that came with no invoice, as the Accountant records it in DEBT-02 (PR 8b): Confirmed, by another transfer.</summary>
    public static async Task<CustomerPayment> RecordPaymentAsync(
        this AuthApiFactory api, Guid customerId, decimal amount, DateOnly? date = null, CustomerPaymentKind kind = CustomerPaymentKind.Payment,
        int statusId = StatusIds.CustomerPayment.Completed)
    {
        var payment = new CustomerPayment
        {
            Code = "PT" + AcsTestKit.NewCode(), CustomerId = customerId, Kind = kind, Amount = amount, Method = CustomerPaymentMethod.OtherTransfer,
            PaymentDate = date ?? PurchaseRequestTestKit.Today, StatusId = statusId,
        };
        await api.QueryAsync(async db =>
        {
            db.CustomerPayments.Add(payment);
            return await db.SaveChangesAsync();
        });
        return payment;
    }

    /// <summary>The invoice and the payments that came with it, <paramref name="days"/> earlier: invoice date and time, payment dates.</summary>
    public static async Task BackDateAsync(this AuthApiFactory api, Guid invoiceId, int days)
    {
        await api.UpdateAsync<SalesInvoice>(invoiceId, i => (i.InvoiceDate, i.InvoicedAt) = (i.InvoiceDate.AddDays(-days), i.InvoicedAt.AddDays(-days)));
        foreach (var payment in await api.PaymentsOfAsync(invoiceId))
        {
            await api.UpdateAsync<CustomerPayment>(payment.Id, p => p.PaymentDate = p.PaymentDate.AddDays(-days));
        }
    }

    private static string Period(DateOnly? from, DateOnly? to) =>
        string.Join('&', new[] { from is { } f ? $"from={f:yyyy-MM-dd}" : null, to is { } t ? $"to={t:yyyy-MM-dd}" : null }.Where(p => p is not null));
}
