using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Purchasing;
using OMTS.Domain.Entities.Sales;
using OMTS.Infrastructure.Payments.Checkout;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;
using static OMTS.Api.IntegrationTests.Sales.VietQrSaleTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>
/// A trader pays an invoice still owed from the Zalo Mini App (decision of 21/11/2026): Checkout SDK with payOS. The four conditions of a paid payment (CLAUDE.md §10):
/// the payOS signature, a checkout whose payment waits, the amount in full (a short transfer is a part payment, Zalo is not told), a bank reference never recorded.
/// </summary>
public class TraderCheckoutTests
{
    private const string MethodId = "PAYOS_QR";
    private const string MiniAppId = "1234567890";
    private const string Webhook = "/api/integrations/payos";

    /// <summary>The API with the fake Checkout providers and keys made for this host (no key-like literal is committed).</summary>
    /// <param name="linkedAccount">Sets the account of the payOS channel (<see cref="OtherAccountNumber"/>) instead of taking the receiving bank account.</param>
    private sealed class CheckoutApi(bool linkedAccount = false) : AuthApiFactory
    {
        public string PrivateKey { get; } = NewSecret();

        public string ChecksumKey { get; } = NewSecret();

        public FakePayOsGateway PayOs => Services.GetRequiredService<FakePayOsGateway>();

        public FakeZaloCheckout Zalo => Services.GetRequiredService<FakeZaloCheckout>();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Checkout:Provider", CheckoutOptions.FakeProvider);
            builder.UseSetting("Checkout:ZaloMiniAppId", MiniAppId);
            builder.UseSetting("Checkout:ZaloPrivateKey", PrivateKey);
            builder.UseSetting("Checkout:ZaloCustomMethodId", MethodId);
            builder.UseSetting("Checkout:PayOsChecksumKey", ChecksumKey);
            if (linkedAccount)
            {
                builder.UseSetting("Checkout:PayOsBankBin", BankBin);
                builder.UseSetting("Checkout:PayOsAccountNumber", OtherAccountNumber);
            }
        }
    }

    private sealed record Setup(SaleSetup Sale, TraderActor Buyer, JsonElement Invoice);

    /// <summary>A trader with a pay-later invoice of 300,000 VND, and the receiving bank account in the cash book.</summary>
    private static async Task<Setup> SetupAsync(CheckoutApi api)
    {
        var sale = await api.SaleSetupAsync();
        await api.UseReceivingAccountAsync();
        var account = await sale.Owner.Client.PostAsJsonAsync("/api/cash-accounts", new { kind = "Bank", bankBin = BankBin, accountNumber = AccountNumber, holderName = "HTX DEMO" });
        await account.ReadAsync(HttpStatusCode.Created);
        var buyer = await api.TraderAsync();
        var invoice = (await sale.Owner.Client.SellAsync(
            Invoice(buyer.Profile.Id, [Line(sale.ProductId, sale.LotId, 10m)], Cash(0m, "PayLater", payLaterOwnerAgreed: true), sale.LotId))).GetProperty("invoice");
        return new Setup(sale, buyer, invoice);
    }

    private static Task<HttpResponseMessage> StartAsync(HttpClient trader, Guid invoiceId) => trader.PostAsync($"/api/me/invoices/{invoiceId}/checkout", null);

    private static async Task<JsonElement> StartedAsync(HttpClient trader, Guid invoiceId, HttpStatusCode expected = HttpStatusCode.Created) =>
        await (await StartAsync(trader, invoiceId)).ReadAsync(expected);

    private static async Task<JsonElement> ViewAsync(HttpClient trader, Guid checkoutId) =>
        await (await trader.GetAsync($"/api/me/checkouts/{checkoutId}")).ReadAsync(HttpStatusCode.OK);

    private static string Hmac(string key, string text) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    /// <summary>A notification as payOS signs it: the <c>data</c> keys in alphabetical order, null as empty.</summary>
    private static byte[] Notification(string checksumKey, long orderCode, decimal amount, string reference, string description, string code = "00")
    {
        var data = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["accountNumber"] = FakePayOsGateway.AccountNumber,
            ["amount"] = amount,
            ["code"] = code,
            ["counterAccountBankId"] = null,
            ["currency"] = "VND",
            ["desc"] = "success",
            ["description"] = description,
            ["orderCode"] = orderCode,
            ["paymentLinkId"] = $"fake-{orderCode}",
            ["reference"] = reference,
            ["transactionDateTime"] = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7)).ToString("yyyy-MM-dd HH:mm:ss"),
        };
        var text = string.Join('&', data.Select(p => $"{p.Key}={p.Value switch { null => "", decimal d => d.ToString(System.Globalization.CultureInfo.InvariantCulture), var v => v.ToString() }}"));
        return JsonSerializer.SerializeToUtf8Bytes(new { code, desc = "success", success = code == "00", data, signature = Hmac(checksumKey, text) });
    }

    private static Task<HttpResponseMessage> NotifyAsync(HttpClient client, byte[] body) =>
        client.PostAsync(Webhook, new ByteArrayContent(body) { Headers = { { "Content-Type", "application/json" } } });

    [Fact]
    public async Task A_trader_pays_an_invoice_in_full_and_Zalo_is_told_once()
    {
        using var api = new CheckoutApi();
        var (_, buyer, invoice) = await SetupAsync(api);
        var invoiceCode = invoice.Text("code")!;

        var started = await StartedAsync(buyer.Client, invoice.Id());
        Assert.Equal((300_000m, "PENDING_TRANSFER", false, false), (started.Money("amount"), started.Text("paymentStatusCode"), started.GetProperty("paidInFull").GetBoolean(), started.GetProperty("expired").GetBoolean()));
        var paymentCode = started.Text("paymentCode")!;
        var qr = started.GetProperty("qr");
        Assert.Equal((paymentCode, 300_000m), (qr.Text("transferContent"), qr.Money("amount")));
        var link = api.PayOs.Links.Last();
        Assert.Equal((300_000L, paymentCode), (link.Amount, link.Description));

        // The Checkout SDK parameters are signed with the private key over the five keys in order, the item as its JSON.
        var order = started.GetProperty("checkout");
        Assert.Equal($"{{\"id\":\"{MethodId}\",\"isCustom\":true}}", order.Text("method"));
        Assert.Equal($"Thanh toan hoa don {invoiceCode}", order.Text("desc"));
        var item = JsonSerializer.Serialize(order.GetProperty("item"));
        Assert.Equal($"[{{\"id\":\"{invoiceCode}\",\"amount\":300000}}]", item);
        var text = $"amount=300000&desc={order.Text("desc")}&extradata={order.Text("extradata")}&item={item}&method={order.Text("method")}";
        Assert.Equal(Hmac(api.PrivateKey, text), order.Text("mac"));

        // The checkout still being paid is handed out again.
        Assert.Equal(started.Id(), (await StartedAsync(buyer.Client, invoice.Id(), HttpStatusCode.OK)).Id());

        var linked = await (await buyer.Client.PostAsJsonAsync($"/api/me/checkouts/{started.Id()}/zalo-order", new { zaloOrderId = "ZALO-ORDER-1" })).ReadAsync(HttpStatusCode.OK);
        Assert.True(linked.GetProperty("zaloOrderLinked").GetBoolean());
        Assert.Empty(api.Zalo.Reports);

        var reference = NewReference();
        var body = Notification(api.ChecksumKey, link.OrderCode, 300_000m, reference, paymentCode);
        var received = await (await NotifyAsync(api.CreateClient(), body)).ReadAsync(HttpStatusCode.OK);
        Assert.Equal("CompletedPayment", received.Text("outcome"));

        var paid = await ViewAsync(buyer.Client, started.Id());
        Assert.Equal(("COMPLETED", true, true, 300_000m), (paid.Text("paymentStatusCode"), paid.GetProperty("paidInFull").GetBoolean(), paid.GetProperty("zaloReported").GetBoolean(), paid.Money("receivedAmount")));
        Assert.Equal(new[] { "ZALO-ORDER-1" }, api.Zalo.Reports);
        var mine = await (await buyer.Client.GetAsync($"/api/me/invoices/{invoice.Id()}")).ReadAsync(HttpStatusCode.OK);
        Assert.True(mine.GetProperty("isPaid").GetBoolean());

        // The same notification again records nothing and tells Zalo nothing more.
        var again = await (await NotifyAsync(api.CreateClient(), body)).ReadAsync(HttpStatusCode.OK);
        Assert.Equal("AlreadyRecorded", again.Text("outcome"));
        Assert.Single(await api.VietQrPaymentsAsync(reference));
        Assert.Single(api.Zalo.Reports);

        // Nothing is left to pay.
        await (await StartAsync(buyer.Client, invoice.Id())).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
    }

    [Fact]
    public async Task Only_a_signed_notification_of_a_known_order_records_and_a_short_transfer_is_a_part_payment()
    {
        using var api = new CheckoutApi();
        var (_, buyer, invoice) = await SetupAsync(api);
        var started = await StartedAsync(buyer.Client, invoice.Id());
        var link = api.PayOs.Links.Last();
        await (await buyer.Client.PostAsJsonAsync($"/api/me/checkouts/{started.Id()}/zalo-order", new { zaloOrderId = "ZALO-ORDER-2" })).ReadAsync(HttpStatusCode.OK);
        var webhook = api.CreateClient();

        // A signature of another key, or a body without one, is refused and read no further.
        Assert.Equal(HttpStatusCode.Unauthorized, (await NotifyAsync(webhook, Notification(NewSecret(), link.OrderCode, 300_000m, NewReference(), "x"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await NotifyAsync(webhook, "{}"u8.ToArray())).StatusCode);

        // payOS's test notification names no checkout; a notification of a payment not made records nothing.
        var unknown = await (await NotifyAsync(webhook, Notification(api.ChecksumKey, 123, 300_000m, NewReference(), "VQRIO123"))).ReadAsync(HttpStatusCode.OK);
        Assert.Equal("unknown-order", unknown.Text("ignored"));
        var failed = await (await NotifyAsync(webhook, Notification(api.ChecksumKey, link.OrderCode, 300_000m, NewReference(), "x", code: "01"))).ReadAsync(HttpStatusCode.OK);
        Assert.Equal("not-successful", failed.Text("ignored"));
        Assert.Equal("PENDING_TRANSFER", (await ViewAsync(buyer.Client, started.Id())).Text("paymentStatusCode"));

        // 100,000 of 300,000: recorded as what arrived, never reported to Zalo as paid.
        var shortBody = Notification(api.ChecksumKey, link.OrderCode, 100_000m, NewReference(), started.Text("paymentCode")!);
        Assert.Equal("CompletedPayment", (await (await NotifyAsync(webhook, shortBody)).ReadAsync(HttpStatusCode.OK)).Text("outcome"));
        var part = await ViewAsync(buyer.Client, started.Id());
        Assert.Equal(("COMPLETED", false, false, 100_000m), (part.Text("paymentStatusCode"), part.GetProperty("paidInFull").GetBoolean(), part.GetProperty("zaloReported").GetBoolean(), part.Money("receivedAmount")));
        Assert.Empty(api.Zalo.Reports);

        // The rest is a new checkout of 200,000.
        var rest = await StartedAsync(buyer.Client, invoice.Id());
        Assert.Equal(200_000m, rest.Money("amount"));
    }

    [Fact]
    public async Task An_expired_checkout_is_replaced_and_money_that_still_arrives_pays_the_invoice()
    {
        using var api = new CheckoutApi();
        var (_, buyer, invoice) = await SetupAsync(api);
        var first = await StartedAsync(buyer.Client, invoice.Id());
        var firstLink = api.PayOs.Links.Last();
        await api.UpdateAsync<TraderCheckout>(first.Id(), c => c.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1));
        Assert.True((await ViewAsync(buyer.Client, first.Id())).GetProperty("expired").GetBoolean());

        var second = await StartedAsync(buyer.Client, invoice.Id());
        Assert.NotEqual(first.Id(), second.Id());
        Assert.Equal("CANCELLED", (await ViewAsync(buyer.Client, first.Id())).Text("paymentStatusCode"));

        // The trader paid the old QR code anyway: the money is recorded on the invoice, nothing is lost.
        var late = Notification(api.ChecksumKey, firstLink.OrderCode, 300_000m, NewReference(), first.Text("paymentCode")!);
        Assert.Equal("PaidInvoice", (await (await NotifyAsync(api.CreateClient(), late)).ReadAsync(HttpStatusCode.OK)).Text("outcome"));
        var mine = await (await buyer.Client.GetAsync($"/api/me/invoices/{invoice.Id()}")).ReadAsync(HttpStatusCode.OK);
        Assert.True(mine.GetProperty("isPaid").GetBoolean());
    }

    [Fact]
    public async Task Only_the_trader_of_the_invoice_starts_and_reads_a_checkout_and_none_starts_without_payOS()
    {
        using var api = new CheckoutApi();
        var (sale, buyer, invoice) = await SetupAsync(api);
        var other = await api.TraderAsync();

        await (await StartAsync(other.Client, invoice.Id())).ReadAsync(HttpStatusCode.NotFound);
        Assert.Equal("MSG-GEN-08", await (await StartAsync(sale.Owner.Client, invoice.Id())).ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, (await StartAsync(api.CreateClient(), invoice.Id())).StatusCode);

        // payOS does not answer: MSG04 and no payment waits.
        api.PayOs.RefuseNext = 1;
        await (await StartAsync(buyer.Client, invoice.Id())).ProblemAsync(HttpStatusCode.ServiceUnavailable, "MSG04");

        var started = await StartedAsync(buyer.Client, invoice.Id());
        await (await other.Client.GetAsync($"/api/me/checkouts/{started.Id()}")).ReadAsync(HttpStatusCode.NotFound);
        await (await other.Client.PostAsJsonAsync($"/api/me/checkouts/{started.Id()}/zalo-order", new { zaloOrderId = "X" })).ReadAsync(HttpStatusCode.NotFound);
        await (await buyer.Client.PostAsJsonAsync($"/api/me/checkouts/{started.Id()}/zalo-order", new { zaloOrderId = "" })).ProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01");

        // Without the Checkout keys (outside Development "Fake" acts the same), no checkout starts.
        using var unset = api.WithWebHostBuilder(b => b.UseSetting("Checkout:Provider", ""));
        var client = unset.CreateClient();
        client.DefaultRequestHeaders.Authorization = buyer.Client.DefaultRequestHeaders.Authorization;
        await (await StartAsync(client, invoice.Id())).ProblemAsync(HttpStatusCode.ServiceUnavailable, "MSG04");
    }

    [Fact]
    public async Task The_payments_go_into_the_cash_book_account_of_the_payOS_channel_when_it_is_set()
    {
        using var api = new CheckoutApi(linkedAccount: true);
        var sale = await api.SaleSetupAsync();
        var buyer = await api.TraderAsync();
        var invoice = (await sale.Owner.Client.SellAsync(
            Invoice(buyer.Profile.Id, [Line(sale.ProductId, sale.LotId, 10m)], Cash(0m, "PayLater", payLaterOwnerAgreed: true), sale.LotId))).GetProperty("invoice");

        // No receiving bank account (staging cannot accept one while no notification service is chosen), and the channel account is not in the cash book yet.
        await (await StartAsync(buyer.Client, invoice.Id())).ProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01");
        var account = await sale.Owner.Client.PostAsJsonAsync("/api/cash-accounts", new { kind = "Bank", bankBin = BankBin, accountNumber = OtherAccountNumber, holderName = "HTX PAYOS" });
        await account.ReadAsync(HttpStatusCode.Created);

        var started = await StartedAsync(buyer.Client, invoice.Id());
        var paymentCode = started.Text("paymentCode")!;
        var body = Notification(api.ChecksumKey, api.PayOs.Links.Last().OrderCode, 300_000m, NewReference(), paymentCode);
        Assert.Equal("CompletedPayment", (await (await NotifyAsync(api.CreateClient(), body)).ReadAsync(HttpStatusCode.OK)).Text("outcome"));
        var into = await api.QueryAsync(db => db.CustomerPayments.AsNoTracking().Where(p => p.Code == paymentCode).Select(p => p.CashAccount!.AccountNumber).SingleAsync());
        Assert.Equal(OtherAccountNumber, into);
        Assert.True((await ViewAsync(buyer.Client, started.Id())).GetProperty("paidInFull").GetBoolean());
    }

    [Fact]
    public async Task A_report_Zalo_refused_is_sent_again_by_the_retry_job()
    {
        using var api = new CheckoutApi();
        var (_, buyer, invoice) = await SetupAsync(api);
        var started = await StartedAsync(buyer.Client, invoice.Id());
        var link = api.PayOs.Links.Last();
        await (await buyer.Client.PostAsJsonAsync($"/api/me/checkouts/{started.Id()}/zalo-order", new { zaloOrderId = "ZALO-ORDER-3" })).ReadAsync(HttpStatusCode.OK);

        api.Zalo.RefuseNext = 1;
        var body = Notification(api.ChecksumKey, link.OrderCode, 300_000m, NewReference(), started.Text("paymentCode")!);
        await (await NotifyAsync(api.CreateClient(), body)).ReadAsync(HttpStatusCode.OK);
        Assert.False((await ViewAsync(buyer.Client, started.Id())).GetProperty("zaloReported").GetBoolean());
        Assert.Empty(api.Zalo.Reports);

        // The job waits a minute after the last report before it sends again.
        await api.UpdateAsync<TraderCheckout>(started.Id(), c => c.ZaloLastAttemptAt = DateTimeOffset.UtcNow.AddMinutes(-2));
        using (var scope = api.Services.CreateScope())
        {
            Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<OMTS.Application.Features.Sales.Checkout.TraderCheckoutReporter>().RetryAsync(10, CancellationToken.None));
        }

        Assert.Equal(new[] { "ZALO-ORDER-3" }, api.Zalo.Reports);
        Assert.True((await ViewAsync(buyer.Client, started.Id())).GetProperty("zaloReported").GetBoolean());
    }
}
