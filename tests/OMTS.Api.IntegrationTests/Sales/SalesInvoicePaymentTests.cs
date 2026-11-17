using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>BR-SALE-07 and BR-DEBT-02 at the port: cash, part in cash, Pay later (with the Owner's agreement), cash above the total up to the debt, and the walk-in customer.</summary>
public class SalesInvoicePaymentTests
{
    // BR-SALE-07 (5): 12,000,000 VND, 4,000,000 in cash and 8,000,000 on the debt when paying later is agreed.
    [Fact]
    public async Task Part_in_cash_and_the_rest_on_pay_later_asks_msg_sale_12_and_puts_the_rest_on_the_debt()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var client = sale.Manager.Client;
        var body = Invoice(sale.CustomerId, [Line(sale.ProductId, sale.LotId, 400m)], Cash(4_000_000m, "PayLater"));

        var (code, field, question) = await (await client.PostInvoiceAsync(body)).QuestionAsync();

        Assert.Equal(("MSG-SALE-12", "payment.payLaterOwnerAgreed"), (code, field));
        Assert.Equal((8_000_000m, 0m), (question.Money("laterAmount"), question.Money("debtAmount")));
        Assert.Contains(" pays 8,000,000 VND of this invoice later? ", question.Text("detail"));
        Assert.EndsWith(" owes 0 VND now.", question.Text("detail"));

        var saved = await client.SellAsync(body.With("payment", Cash(4_000_000m, "PayLater", payLaterOwnerAgreed: true)));
        var invoice = saved.GetProperty("invoice");
        Assert.Equal(("PayLater", 12_000_000m), (invoice.Text("paymentTerm"), invoice.Money("total")));
        Assert.Equal(4_000_000m, Assert.Single(invoice.GetProperty("payments").EnumerateArray()).Money("amount"));
        var customerName = invoice.GetProperty("customer").Text("name");
        Assert.Equal($"Invoice {invoice.Text("code")} is completed: 4,000,000 VND collected in cash; 8,000,000 VND added to the debt of {customerName}.", saved.Text("message"));
        Assert.Equal(8_000_000m, await client.CustomerDebtAsync(sale.CustomerId));

        // All on Pay later: no payment at all, and the debt now asked about is the 8,000,000 already owed.
        var later = Invoice(sale.CustomerId, [Line(sale.ProductId, sale.OtherLotId, 10m)], Cash(0m, "PayLater"));
        var (_, _, again) = await (await client.PostInvoiceAsync(later)).QuestionAsync();
        Assert.Equal(8_000_000m, again.Money("debtAmount"));
        var allLater = (await client.SellAsync(later.With("payment", Cash(0m, "PayLater", payLaterOwnerAgreed: true)))).GetProperty("invoice");
        Assert.Empty(allLater.GetProperty("payments").EnumerateArray());
        Assert.Equal(8_300_000m, await client.CustomerDebtAsync(sale.CustomerId));
    }

    [Fact]
    public async Task Cash_below_the_total_with_nothing_for_the_rest_is_refused_and_a_walk_in_customer_cannot_pay_later()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var client = sale.Manager.Client;

        Assert.Equal(["Cash amount"], await (await client.PostInvoiceAsync(Invoice(sale.CustomerId, [Line(sale.ProductId, sale.LotId, 10m)], Cash(100_000m)))).InvalidFieldsAsync());
        Assert.Equal(["Cash amount"], await (await client.PostInvoiceAsync(Invoice(null, [Line(sale.ProductId, sale.LotId, 10m)], Cash(100_000m)))).InvalidFieldsAsync());
        Assert.Equal(["Pay later"], await (await client.PostInvoiceAsync(Invoice(null, [Line(sale.ProductId, sale.LotId, 10m)], Cash(0m, "PayLater", payLaterOwnerAgreed: true)))).InvalidFieldsAsync());
        Assert.Equal(0, await api.QueryAsync(db => db.SalesInvoices.CountAsync()));
    }

    // SALE-05.6 step 3 and BR-SALE-07 (4): the extra lowers the debt only up to the debt before the invoice; the rest is change.
    [Fact]
    public async Task Cash_above_the_total_asks_msg_sale_11_and_lowers_the_debt_at_most_to_zero()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var client = sale.Manager.Client;
        await client.SellAsync(Invoice(sale.CustomerId, [Line(sale.ProductId, sale.LotId, 10m)], Cash(0m, "PayLater", payLaterOwnerAgreed: true), sale.LotId));
        Assert.Equal(300_000m, await client.CustomerDebtAsync(sale.CustomerId));

        var body = Invoice(sale.CustomerId, [Line(sale.ProductId, sale.LotId, 100m)], Cash(4_000_000m), sale.LotId);
        var (code, field, question) = await (await client.PostInvoiceAsync(body)).QuestionAsync();

        Assert.Equal(("MSG-SALE-11", "payment.subtractExtraFromDebt"), (code, field));
        Assert.Equal((1_000_000m, 300_000m, 300_000m, 700_000m), (question.Money("excessAmount"), question.Money("debtAmount"), question.Money("subtractAmount"), question.Money("changeAmount")));
        Assert.StartsWith("The trader paid 1,000,000 VND more than the total.", question.Text("detail"));
        Assert.EndsWith("owes 300,000 VND. Subtract 300,000 VND from this debt and give back 700,000 VND as change?", question.Text("detail"));

        var saved = await client.SellAsync(body.With("payment", Cash(4_000_000m, subtractExtraFromDebt: true)));
        Assert.Equal((700_000m, 300_000m), (saved.Money("change"), saved.Money("subtractedFromDebt")));
        Assert.Equal(3_300_000m, Assert.Single(saved.GetProperty("invoice").GetProperty("payments").EnumerateArray()).Money("amount"));
        Assert.Equal(0m, await client.CustomerDebtAsync(sale.CustomerId));

        // With no debt left nothing is asked: the extra is change, and cash never creates money owed back.
        var noDebt = await client.SellAsync(Invoice(sale.CustomerId, [Line(sale.ProductId, sale.LotId, 10m)], Cash(500_000m), sale.LotId));
        Assert.Equal((200_000m, 0m), (noDebt.Money("change"), noDebt.Money("subtractedFromDebt")));
        Assert.Equal(300_000m, Assert.Single(noDebt.GetProperty("invoice").GetProperty("payments").EnumerateArray()).Money("amount"));
        Assert.Equal(0m, await client.CustomerDebtAsync(sale.CustomerId));

        // Answered No: the whole extra is change.
        await client.SellAsync(Invoice(sale.CustomerId, [Line(sale.ProductId, sale.LotId, 10m)], Cash(0m, "PayLater", payLaterOwnerAgreed: true), sale.LotId));
        var answeredNo = await client.SellAsync(Invoice(sale.CustomerId, [Line(sale.ProductId, sale.LotId, 10m)], Cash(1_000_000m, subtractExtraFromDebt: false), sale.LotId));
        Assert.Equal((700_000m, 0m), (answeredNo.Money("change"), answeredNo.Money("subtractedFromDebt")));
        Assert.Equal(300_000m, await client.CustomerDebtAsync(sale.CustomerId));

        // A walk-in customer has no debt: no question, change.
        var walkIn = await client.SellAsync(Invoice(null, [Line(sale.ProductId, sale.LotId, 10m)], Cash(400_000m), sale.LotId));
        Assert.Equal(100_000m, walkIn.Money("change"));
    }
}
