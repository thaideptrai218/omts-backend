using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Persistence;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>
/// MSG-SALE-15 "paid / to pay" and the DEBT-01 count of unpaid invoices read the BR-DEBT-05 allocation: a payment with no invoice covers the oldest invoices of the customer,
/// so the to-pay parts of the invoices add up to the debt.
/// </summary>
public class CustomerDebtInvoiceMessageTests
{
    [Fact]
    public async Task The_invoice_message_and_the_unpaid_count_follow_the_payments_allocated_oldest_first()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        await api.SetReceivingAccountAsync();
        var client = sale.Manager.Client;
        var older = (await client.SellAsync(Invoice(sale.CustomerId, [Line(sale.ProductId, sale.LotId, 10m)], Cash(0m, "PayLater", payLaterOwnerAgreed: true), sale.LotId))).GetProperty("invoice");
        await api.BackDateAsync(older.Id(), 2);
        await client.FinishedAsync(older.Id(), wantsQrCode: false);
        var newer = (await client.SellAsync(Invoice(sale.CustomerId, [Line(sale.ProductId, sale.LotId, 20m)], Cash(100_000m, "PayLater", payLaterOwnerAgreed: true), sale.LotId))).GetProperty("invoice");
        // A transfer with no invoice (DEBT-02): it pays the older invoice (300,000) first, the remaining 100,000 goes to the newer one.
        await api.RecordPaymentAsync(sale.CustomerId, 400_000m);
        var phone = newer.GetProperty("customer").Text("phoneNumber")!;

        await client.FinishedAsync(newer.Id(), wantsQrCode: false);

        var code = newer.Text("code");
        Assert.Contains($"invoice {code} of ", api.SmsTo(phone)[0].Text);
        Assert.EndsWith($"600,000 VND, paid 200,000 VND; to pay 400,000 VND to account {SalesInvoiceSendingTestKit.DemoAccountNumber} ({SalesInvoiceSendingTestKit.DemoBankName}), transfer content {code}.",
            api.SmsTo(phone)[0].Text);
        // The older invoice's message still names the account, but the transfer has paid it: only the newer one counts (BR-DEBT-01).
        Assert.Equal(1, await UnpaidWithSentCodesAsync(api));
        Assert.Equal(400_000m, await client.CustomerDebtAsync(sale.CustomerId));

        // The rest paid by another transfer: the Resend says paid in full, and no invoice is unpaid any more.
        await api.RecordPaymentAsync(sale.CustomerId, 400_000m);
        (await client.ResendAsync(newer.Id())).EnsureSuccessStatusCode();
        Assert.EndsWith("600,000 VND, paid 600,000 VND.", api.SmsTo(phone)[0].Text);
        Assert.Equal(0, await UnpaidWithSentCodesAsync(api));
    }

    private static async Task<int> UnpaidWithSentCodesAsync(AuthApiFactory api)
    {
        using var scope = api.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ISentInvoiceQrCodeQuery>().CountUnpaidInvoicesWithSentCodesAsync(CancellationToken.None);
    }
}
