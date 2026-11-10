using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Sales;
using OMTS.Infrastructure.Payments;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>SALE-05 steps 7-8 and API-13 calls. The receiving account is fictional (9999…); every webhook secret is made at run time.</summary>
public static class VietQrSaleTestKit
{
    public const string AccountNumber = "999900000001";
    public const string OtherAccountNumber = "999900000002";
    public const string BankBin = "970436";
    public const string WebhookRoute = "/api/integrations/payment-notifications";
    public const string SecretKey = "Payments:Fake:WebhookSecret";

    /// <summary>A fresh secret per host, never a literal.</summary>
    public static string NewSecret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    public static string NewReference() => "FT-DEMO-" + AcsTestKit.NewCode();

    /// <summary>The cooperative's receiving bank account, as DEBT-01 leaves it once the service accepted it.</summary>
    public static async Task UseReceivingAccountAsync(this AuthApiFactory api, string number = AccountNumber)
    {
        var cooperative = await api.EnsureCooperativeAsync();
        await api.UpdateAsync<Cooperative>(cooperative.Id, c => c.BankAccount = new ReceivingBankAccount
        {
            BankBin = BankBin, BankName = "Vietcombank", AccountNumber = number, AccountHolderName = "HTX DEMO", AcceptedAt = DateTimeOffset.UtcNow,
        });
    }

    /// <summary>The rest after <paramref name="cash"/> is paid with the VietQR code of the invoice.</summary>
    public static object VietQr(decimal cash = 0m) => SalesInvoiceTestKit.Cash(cash, "VietQr");

    /// <summary>Posts the sale and returns the sale awaiting its transfer (202).</summary>
    public static async Task<JsonElement> AwaitTransferAsync(this HttpClient client, object body) =>
        await (await client.PostInvoiceAsync(body)).ReadAsync(HttpStatusCode.Accepted);

    public static Task<HttpResponseMessage> PollAsync(this HttpClient client, string code) => client.GetAsync($"/api/sales-invoices/pending/{code}");

    public static async Task<JsonElement> PolledAsync(this HttpClient client, string code) => await (await client.PollAsync(code)).ReadAsync(HttpStatusCode.OK);

    public static Task<HttpResponseMessage> ChangePaymentAsync(this HttpClient client, string code, object payment) =>
        client.PostAsJsonAsync($"/api/sales-invoices/pending/{code}/change-payment", new { payment });

    public static Task<HttpResponseMessage> StopAsync(this HttpClient client, string code, bool confirmed) =>
        client.PostAsJsonAsync($"/api/sales-invoices/pending/{code}/stop", new { confirmed });

    public static string Code(this JsonElement pending) => pending.GetProperty("code").GetString()!;

    /// <summary>The fake service reports a transfer (Development only endpoint, anonymous).</summary>
    public static async Task<JsonElement> TransferAsync(this AuthApiFactory api, string? invoiceCode, decimal amount, string? reference = null, string? content = null, string? account = null)
    {
        using var client = api.CreateClient();
        var response = await client.PostAsJsonAsync("/api/dev/payment-notifications", new { invoiceCode, amount, bankReference = reference ?? NewReference(), content, accountNumber = account });
        return Assert.Single((await response.ReadAsync(HttpStatusCode.OK)).GetProperty("transactions").EnumerateArray());
    }

    public static string Outcome(this JsonElement result) => result.GetProperty("outcome").GetString()!;

    /// <summary>A host whose fake service signs its notifications with <paramref name="secret"/>.</summary>
    public static WebApplicationFactory<Program> WithWebhookSecret(this AuthApiFactory api, string secret) =>
        api.WithWebHostBuilder(b => b.UseSetting(SecretKey, secret));

    /// <summary>The body of a notification in the generic format, one transfer to the receiving account.</summary>
    public static byte[] NotificationBody(string reference, decimal amount, string content, string account = AccountNumber, string direction = "In") =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            transactions = new[] { new { bankReference = reference, accountNumber = account, amount, content, occurredAt = DateTimeOffset.UtcNow, direction } },
        }));

    /// <summary>Posts a notification signed with <paramref name="secret"/> (null: no signature header).</summary>
    public static async Task<HttpResponseMessage> NotifyAsync(this HttpClient client, byte[] body, string? secret)
    {
        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        if (secret is not null)
        {
            content.Headers.Add(FakeBankNotificationParser.SignatureHeader, FakeBankNotificationParser.Sign(body, secret));
        }

        return await client.PostAsync(WebhookRoute, content);
    }

    public static Task<List<CustomerPayment>> VietQrPaymentsAsync(this AuthApiFactory api, string reference) =>
        api.QueryAsync(db => db.CustomerPayments.AsNoTracking().Where(p => p.BankReference == reference).ToListAsync());

    public static Task<PendingSale> PendingRowAsync(this AuthApiFactory api, string code) =>
        api.QueryAsync(db => db.PendingSales.AsNoTracking().SingleAsync(s => s.Code == code));
}
