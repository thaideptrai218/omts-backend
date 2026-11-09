using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;
using static OMTS.Api.IntegrationTests.Sales.VietQrSaleTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>SALE-05.0.E4: the transfer has not arrived, and the Zone Manager changes the payment method or stops the sale; who may see and continue a sale.</summary>
public class VietQrPendingSaleActionsTests
{
    // SALE-05.0.E4 step 2 and BR-SALE-07 (2): the invoice keeps its code; a transfer that still arrives lowers the debt, here into money owed back.
    [Fact]
    public async Task Changing_to_cash_saves_the_invoice_with_the_same_code_and_a_late_transfer_pays_it()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        await api.UseReceivingAccountAsync();
        var client = sale.Manager.Client;
        var code = (await client.AwaitTransferAsync(Invoice(sale.CustomerId, [Line(sale.ProductId, sale.LotId, 10m)], VietQr()))).Code();

        Assert.Equal(["Payment"], await (await client.ChangePaymentAsync(code, VietQr())).InvalidFieldsAsync());
        var saved = await (await client.ChangePaymentAsync(code, Cash(300_000m))).ReadAsync(HttpStatusCode.Created);

        var invoice = saved.GetProperty("invoice");
        Assert.Equal((code, "PayNow"), (invoice.Text("code"), invoice.Text("paymentTerm")));
        Assert.Equal($"Invoice {code} is completed: 300,000 VND collected in cash.", saved.Text("message"));
        var polled = await client.PolledAsync(code);
        Assert.Equal(("PaymentChanged", invoice.Id()), (polled.Text("status"), polled.GetProperty("invoiceId").GetGuid()));
        Assert.Equal(JsonValueKind.Null, polled.GetProperty("vietQr").ValueKind);
        var again = await (await client.ChangePaymentAsync(code, Cash(300_000m))).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal(("MSG-GEN-09", $"Sale {code} is Completed and cannot be used for this action."), (again.Text("code"), again.Text("detail")));
        Assert.Equal("MSG-GEN-09", await (await client.StopAsync(code, confirmed: true)).ErrorCodeAsync());

        var late = await api.TransferAsync(code, 300_000m);

        Assert.Equal("PaidInvoice", late.Outcome());
        Assert.Equal(-300_000m, await client.CustomerDebtAsync(sale.CustomerId));
        var payments = await api.PaymentsOfAsync(invoice.Id());
        Assert.Equal([CustomerPaymentMethod.VietQrInvoice, CustomerPaymentMethod.Cash], payments.Select(p => p.Method).OrderBy(m => m).ToList());
        Assert.All(payments, p => Assert.Equal(sale.CustomerId, p.CustomerId));
    }

    // SALE-05.0.E4 step 2 to Pay later: asked as at the port (MSG-SALE-12), with the cash part.
    [Fact]
    public async Task Changing_to_part_cash_and_pay_later_asks_msg_sale_12_and_puts_the_rest_on_the_debt()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        await api.UseReceivingAccountAsync();
        var client = sale.Manager.Client;
        var code = (await client.AwaitTransferAsync(Invoice(sale.CustomerId, [Line(sale.ProductId, sale.LotId, 100m)], VietQr(1_000_000m)))).Code();

        var (question, field, _) = await (await client.ChangePaymentAsync(code, Cash(1_000_000m, "PayLater"))).QuestionAsync();
        Assert.Equal(("MSG-SALE-12", "payment.payLaterOwnerAgreed"), (question, field));

        var saved = await (await client.ChangePaymentAsync(code, Cash(1_000_000m, "PayLater", payLaterOwnerAgreed: true))).ReadAsync(HttpStatusCode.Created);

        Assert.Equal(("PayLater", code), (saved.GetProperty("invoice").Text("paymentTerm"), saved.GetProperty("invoice").Text("code")));
        Assert.Equal(2_000_000m, await client.CustomerDebtAsync(sale.CustomerId));
    }

    // SALE-05.0.E4 step 3, BR-SALE-08: MSG-SALE-14 first; then nothing saved but the code in the audit log; a transfer that still arrives has no customer (GFR-08).
    [Fact]
    public async Task Stop_sale_asks_msg_sale_14_keeps_the_code_in_the_audit_log_and_a_later_transfer_is_a_payment_without_a_customer()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        await api.UseReceivingAccountAsync();
        var client = sale.Manager.Client;
        var code = (await client.AwaitTransferAsync(Invoice(sale.CustomerId, [Line(sale.ProductId, sale.LotId, 10m)], VietQr(100_000m)))).Code();

        var (question, field, asked) = await (await client.StopAsync(code, confirmed: false)).QuestionAsync();
        Assert.Equal(("MSG-SALE-14", "confirmed"), (question, field));
        Assert.Equal(
            $"The transfer for invoice {code} has not arrived. Stop this sale? Nothing is saved, and code {code} is kept in the audit log. Give back any cash and keep the oysters. If the transfer still arrives, the Accountant pays it back.",
            asked.Text("detail"));
        Assert.Equal(PendingSaleStatus.Awaiting, (await api.PendingRowAsync(code)).Status);

        var stopped = await (await client.StopAsync(code, confirmed: true)).ReadAsync(HttpStatusCode.OK);

        Assert.Equal("Stopped", stopped.Text("status"));
        var audit = await api.QueryAsync(db => db.AuditEvents.AsNoTracking().SingleAsync(e => e.Action == "SalesInvoice.SaleStopped" && e.RecordCode == code));
        Assert.Equal(("Stop sale", sale.Manager.Account.Id), (audit.Reason, audit.ActorAccountId!.Value));
        Assert.Equal((0, 0), await api.QueryAsync(async db => (await db.SalesInvoices.CountAsync(), await db.CustomerPayments.CountAsync())));
        Assert.Equal("MSG-GEN-09", await (await client.StopAsync(code, confirmed: true)).ErrorCodeAsync());
        Assert.Equal("MSG-GEN-09", await (await client.ChangePaymentAsync(code, Cash(300_000m))).ErrorCodeAsync());

        var reference = NewReference();
        Assert.Equal("WithoutCustomer", (await api.TransferAsync(code, 200_000m, reference)).Outcome());

        var payment = Assert.Single(await api.VietQrPaymentsAsync(reference));
        Assert.Equal(((Guid?)null, (Guid?)null, code, 200_000m), (payment.CustomerId, payment.InvoiceId, payment.TransferInvoiceCode, payment.Amount));
        Assert.Equal(0m, await client.CustomerDebtAsync(sale.CustomerId));
        var accountant = await api.AllZonesAccountantAsync();
        var row = Assert.Single(await accountant.Client.AwaitingRowsAsync());
        Assert.Equal((code, JsonValueKind.Null), (row.Text("transferInvoiceCode"), row.GetProperty("customer").ValueKind));
    }

    // SALES_INVOICES Create in the zone of the sale (BR-SALE-08): the other zone, the Accountant, the trader and anonymous callers cannot see or continue it.
    [Fact]
    public async Task Only_sellers_of_the_zone_see_change_and_stop_a_sale_awaiting_its_transfer()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        await api.UseReceivingAccountAsync();
        var body = Invoice(sale.CustomerId, [Line(sale.ProductId, sale.LotId, 10m)], VietQr());
        var code = (await sale.Manager.Client.AwaitTransferAsync(body)).Code();
        var otherZone = await api.ManagerAsync((await sale.Owner.Client.CreateZoneAsync()).Id());
        var accountant = await api.AllZonesAccountantAsync();
        var trader = await api.TraderAsync();

        foreach (var outsider in new[] { otherZone.Client, accountant.Client, trader.Client })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await outsider.PollAsync(code)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await outsider.StopAsync(code, confirmed: true)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await outsider.ChangePaymentAsync(code, Cash(300_000m))).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await accountant.Client.PostInvoiceAsync(body)).StatusCode);
        using var anonymous = api.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PollAsync(code)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await sale.Manager.Client.PollAsync("HD999999")).StatusCode);
        Assert.Empty((await otherZone.Client.GetFromJsonAsync<JsonElement[]>("/api/sales-invoices/pending"))!);
        Assert.Equal([code], (await sale.Manager.Client.GetFromJsonAsync<JsonElement[]>("/api/sales-invoices/pending"))!.Select(p => p.Code()));
        Assert.Equal("Awaiting", (await sale.Owner.Client.PolledAsync(code)).Text("status"));
        Assert.Equal(PendingSaleStatus.Awaiting, (await api.PendingRowAsync(code)).Status);
    }
}
