using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Infrastructure.Payments;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;
using static OMTS.Api.IntegrationTests.Sales.VietQrSaleTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>API-13 and GFR-08: who may report transfers, which ones are recorded, and to whom they belong.</summary>
public class BankNotificationWebhookTests
{
    // API-13 is anonymous: only a notification signed with the configured secret is read.
    [Fact]
    public async Task A_missing_or_wrong_signature_answers_401_and_records_nothing()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        await api.UseReceivingAccountAsync();
        var code = (await sale.Manager.Client.AwaitTransferAsync(Invoice(sale.CustomerId, [Line(sale.ProductId, sale.LotId, 10m)], VietQr()))).Code();
        var secret = NewSecret();
        var body = NotificationBody(NewReference(), 300_000m, code);
        using var hooked = api.WithWebhookSecret(secret);
        using var bank = hooked.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await bank.NotifyAsync(body, secret: null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await bank.NotifyAsync(body, NewSecret())).StatusCode);
        // Signed for another body: the amount changed on the way.
        using (var tampered = new ByteArrayContent(NotificationBody(NewReference(), 1m, code)))
        {
            tampered.Headers.Add(FakeBankNotificationParser.SignatureHeader, FakeBankNotificationParser.Sign(body, secret));
            Assert.Equal(HttpStatusCode.Unauthorized, (await bank.PostAsync(WebhookRoute, tampered)).StatusCode);
        }

        // No secret configured (the default), or no service (None): nothing is authentic.
        using var plain = api.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await plain.NotifyAsync(body, secret)).StatusCode);
        using var none = api.WithWebHostBuilder(b => b.UseSetting(SecretKey, secret).UseSetting("Payments:Provider", "None"));
        using var noneClient = none.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await noneClient.NotifyAsync(body, secret)).StatusCode);
        Assert.Equal(0, await api.QueryAsync(db => db.CustomerPayments.CountAsync()));

        // A signed body that is not a notification is 400; the right one is read.
        var garbage = Encoding.UTF8.GetBytes("{\"transactions\":[]}");
        Assert.Equal(HttpStatusCode.BadRequest, (await bank.NotifyAsync(garbage, secret)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await bank.NotifyAsync(body, secret)).StatusCode);
        Assert.Equal(Domain.Enums.PendingSaleStatus.Completed, (await api.PendingRowAsync(code)).Status);
    }

    // GFR-08 "Afterwards" and BR-DEBT-01: transfers without an invoice code, to another account or out of the account are not recorded; an unknown code has no customer.
    [Fact]
    public async Task Only_incoming_transfers_to_the_receiving_account_with_an_invoice_code_are_recorded()
    {
        using var api = new AuthApiFactory();
        await api.SaleSetupAsync();
        await api.UseReceivingAccountAsync();
        var secret = NewSecret();
        using var hooked = api.WithWebhookSecret(secret);
        using var bank = hooked.CreateClient();

        Assert.Equal(("Ignored", "NoInvoiceCode"), Result(await api.TransferAsync(null, 500_000m, content: "Tien hang thang 10")));
        Assert.Equal(("Ignored", "NotTheReceivingAccount"), Result(await api.TransferAsync("HD000001", 500_000m, account: OtherAccountNumber)));
        var outgoing = await (await bank.NotifyAsync(NotificationBody(NewReference(), 500_000m, "HD000001", direction: "Out"), secret)).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(("Ignored", "Outgoing"), Result(Assert.Single(outgoing.GetProperty("transactions").EnumerateArray())));
        Assert.Equal(0, await api.QueryAsync(db => db.CustomerPayments.CountAsync()));

        var reference = NewReference();
        Assert.Equal(("WithoutCustomer", (string?)null), Result(await api.TransferAsync(null, 500_000m, reference, content: "CT HD-999999 tu DEMO")));
        var payment = Assert.Single(await api.VietQrPaymentsAsync(reference));
        Assert.Equal(("HD999999", (Guid?)null, Domain.Enums.CustomerPaymentMethod.VietQrInvoice), (payment.TransferInvoiceCode, payment.CustomerId, payment.Method));
        Assert.Equal(Domain.Constants.StatusIds.CustomerPayment.AwaitingConfirmation, payment.StatusId);
    }

    private static (string, string?) Result(System.Text.Json.JsonElement result) => (result.Outcome(), result.Text("reason"));

    // BR-SALE-07 (1): a transfer short of the amount does not complete the sale (it waits, its money recorded with no customer); one above it completes it with all of it.
    [Fact]
    public async Task A_short_transfer_leaves_the_sale_awaiting_and_a_larger_one_completes_it_with_its_whole_amount()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        await api.UseReceivingAccountAsync();
        var client = sale.Manager.Client;
        var code = (await client.AwaitTransferAsync(Invoice(null, [Line(sale.ProductId, sale.LotId, 10m)], VietQr()))).Code();

        Assert.Equal("WithoutCustomer", (await api.TransferAsync(code, 100_000m)).Outcome());

        var polled = await client.PolledAsync(code);
        Assert.Equal(("Awaiting", 100_000m, 300_000m), (polled.Text("status"), polled.Money("receivedAmount"), polled.Money("toPayAmount")));

        var reference = NewReference();
        Assert.Equal("CompletedSale", (await api.TransferAsync(code, 350_000m, reference)).Outcome());

        var transfer = Assert.Single(await api.VietQrPaymentsAsync(reference));
        Assert.Equal((350_000m, (Guid?)null), (transfer.Amount, transfer.CustomerId));
        Assert.Equal($"Invoice {code} is completed: 350,000 VND received by VietQR.", (await client.PolledAsync(code)).GetProperty("invoice").Text("message"));
    }

    // BR-SALE-07 (7): a Pay later invoice is paid later with its VietQR code; the payment is of its customer and lowers the debt (BR-DEBT-02).
    [Fact]
    public async Task A_later_transfer_with_the_code_of_a_pay_later_invoice_pays_it()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        await api.UseReceivingAccountAsync();
        var invoice = await sale.PayLaterAsync(sale.CustomerId, 10m);
        Assert.Equal(300_000m, await sale.Manager.Client.CustomerDebtAsync(sale.CustomerId));

        var reference = NewReference();
        Assert.Equal("PaidInvoice", (await api.TransferAsync(invoice.Text("code"), 300_000m, reference)).Outcome());

        var payment = Assert.Single(await api.VietQrPaymentsAsync(reference));
        Assert.Equal((invoice.Id(), sale.CustomerId), (payment.InvoiceId!.Value, payment.CustomerId!.Value));
        Assert.Equal(0m, await sale.Manager.Client.CustomerDebtAsync(sale.CustomerId));
    }
}
