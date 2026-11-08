using System.Net;
using ClosedXML.Excel;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>QA of DEBT-03 (PR 8a): totals, walk-in sales, MSG-SALE-15 and the DEBT-01 count after 8a, the export cells and anonymous callers.</summary>
public class CustomerDebtQaTests
{
    private static object PayLater(Guid customerId, SaleSetup sale, decimal kg, decimal cash = 0m) =>
        Invoice(customerId, [Line(sale.ProductId, sale.LotId, kg)], Cash(cash, "PayLater", payLaterOwnerAgreed: true), sale.LotId);

    [Fact]
    public async Task Anonymous_callers_get_401_on_the_list_the_statement_and_the_export()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var anonymous = api.CreateClient();

        foreach (var response in new[] { await anonymous.GetDebtsAsync(), await anonymous.GetStatementAsync(sale.CustomerId), await anonymous.ExportDebtsAsync(sale.CustomerId) })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    // BR-FARM-10: "Receivables are the total of the customer debts above zero ... always cooperative-wide ... DEBT-03 uses the same definitions" (DEBT-03 step 1:
    // "the total receivables and the total money owed back"). The search (added by the developer, not in the SRS) narrows the rows, not the totals.
    [Fact]
    public async Task The_list_totals_stay_the_cooperative_totals_when_a_search_narrows_the_rows()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var owner = sale.Owner.Client;
        await sale.Manager.Client.SellAsync(PayLater(sale.CustomerId, sale, 10m));
        var target = (await owner.CreateCustomerAsync("Searched " + AcsTestKit.NewCode())).Id();
        await sale.Manager.Client.SellAsync(PayLater(target, sale, 20m));

        var all = await owner.DebtListAsync();
        var searched = await owner.DebtListAsync("?search=Searched");

        Assert.Equal(900_000m, all.Money("receivablesTotal"));
        Assert.Equal([target], searched.Rows().Select(r => r.GetProperty("customer").Id()).ToList());
        Assert.Equal(all.Money("receivablesTotal"), searched.Money("receivablesTotal"));
    }

    // BR-DEBT-02 Walk-in customer: the invoice and its payments count in no customer debt; its cash still awaits the Accountant's confirmation (BR-DEBT-03).
    [Fact]
    public async Task A_walk_in_sale_counts_in_no_debt_and_its_cash_still_awaits_confirmation()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        await sale.Manager.Client.SoldAsync(sale, sale.LotId, 10m, walkIn: true);

        var list = await sale.Owner.Client.DebtListAsync();
        var statement = await sale.Owner.Client.StatementAsync(sale.CustomerId);

        Assert.Empty(list.Rows());
        Assert.Equal(("MSG-GEN-11", 0m, 1), (list.Text("messageCode"), list.Money("receivablesTotal"), list.GetProperty("awaitingConfirmationCount").GetInt32()));
        Assert.Equal((0, 0m), (statement.GetProperty("entries").GetArrayLength(), statement.Money("debtToday")));
    }

    // BR-DEBT-02 money owed back "is left, and the next invoices of the customer use it up"; MSG-SALE-15 paid / to pay follow BR-DEBT-05 (Σ to pay = debt).
    [Fact]
    public async Task The_invoice_message_counts_money_owed_back_from_an_earlier_transfer_as_paid()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        await api.SetReceivingAccountAsync();
        var client = sale.Manager.Client;
        var older = (await client.SellAsync(PayLater(sale.CustomerId, sale, 10m))).GetProperty("invoice");
        await api.BackDateAsync(older.Id(), 3);
        await api.RecordPaymentAsync(sale.CustomerId, 400_000m, PurchaseRequestTestKit.Today.AddDays(-2));
        Assert.Equal(-100_000m, await client.CustomerDebtAsync(sale.CustomerId));
        var newer = (await client.SellAsync(PayLater(sale.CustomerId, sale, 20m))).GetProperty("invoice");

        await client.FinishedAsync(newer.Id(), wantsQrCode: false);

        var code = newer.Text("code");
        var sms = api.SmsTo(newer.GetProperty("customer").Text("phoneNumber")!)[0].Text;
        Assert.EndsWith($"600,000 VND, paid 100,000 VND; to pay 500,000 VND to account {SalesInvoiceSendingTestKit.DemoAccountNumber} ({SalesInvoiceSendingTestKit.DemoBankName}), transfer content {code}.", sms);
        Assert.Equal([(code, 500_000m)], (await sale.Owner.Client.StatementAsync(sale.CustomerId)).Unpaid());
    }

    // BR-SALE-11 / BR-DEBT-05 (3): cash kept on a cancelled invoice covers the oldest invoice, so the DEBT-01 count (BR-DEBT-01) no longer counts it as unpaid;
    // BR-DEBT-04: the cancelled invoice stays in the statement, marked Cancelled, and counts 0.
    [Fact]
    public async Task Cash_kept_on_a_cancelled_invoice_pays_the_oldest_sent_invoice_and_leaves_the_unpaid_count()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        await api.SetReceivingAccountAsync();
        var client = sale.Manager.Client;
        var older = (await client.SellAsync(PayLater(sale.CustomerId, sale, 10m))).GetProperty("invoice");
        await api.BackDateAsync(older.Id(), 2);
        await client.FinishedAsync(older.Id(), wantsQrCode: false);
        Assert.Equal(1, await UnpaidWithSentCodesAsync(api));
        var cancelled = await client.SoldAsync(sale, sale.LotId, 10m);

        await api.UpdateAsync<SalesInvoice>(cancelled.Id(), i => i.StatusId = StatusIds.SalesInvoice.Cancelled);

        Assert.Equal(0, await UnpaidWithSentCodesAsync(api));
        var statement = await sale.Owner.Client.StatementAsync(sale.CustomerId, PurchaseRequestTestKit.Today.AddDays(-5), PurchaseRequestTestKit.Today);
        Assert.Equal((0m, 0), (statement.Money("debtToday"), statement.GetProperty("unpaidInvoices").GetArrayLength()));
        var entry = statement.GetProperty("entries").EnumerateArray().Single(e => e.Text("code") == cancelled.Text("code"));
        Assert.Equal((true, 0m), (entry.GetProperty("cancelled").GetBoolean(), entry.Money("debtChange")));
    }

    // DEBT-03.2: the file holds what is shown; a name that looks like a formula stays text (no formula injection); money and ages are numbers, dates are dates.
    [Fact]
    public async Task The_export_keeps_a_formula_like_name_as_text_and_writes_money_ages_and_dates_as_values()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        const string name = "=HYPERLINK(\"https://example.invalid\",\"DEMO\")";
        await api.UpdateAsync<Customer>(sale.CustomerId, c => c.Name = name);
        var invoice = (await sale.Manager.Client.SellAsync(PayLater(sale.CustomerId, sale, 10m))).GetProperty("invoice");
        await api.BackDateAsync(invoice.Id(), 4);

        var file = await sale.Owner.Client.ExportDebtsAsync(sale.CustomerId, PurchaseRequestTestKit.Today.AddDays(-10), PurchaseRequestTestKit.Today);

        using var workbook = new XLWorkbook(await file.Content.ReadAsStreamAsync());
        var row = workbook.Worksheet("Customer debts").Row(2);
        Assert.False(row.Cell(1).HasFormula);
        Assert.Equal((XLDataType.Text, name), (row.Cell(1).DataType, row.Cell(1).GetString()));
        Assert.Equal((XLDataType.Number, 300_000m), (row.Cell(3).DataType, row.Cell(3).GetValue<decimal>()));
        Assert.Equal((XLDataType.Number, 4), (row.Cell(4).DataType, row.Cell(4).GetValue<int>()));
        Assert.Equal(XLDataType.DateTime, row.Cell(5).DataType);
        Assert.Equal(PurchaseRequestTestKit.Today.AddDays(-4).ToDateTime(TimeOnly.MinValue), row.Cell(5).GetDateTime());
        var opening = workbook.Worksheet("Statement").Row(2);
        Assert.Equal((false, name), (opening.Cell(3).HasFormula, opening.Cell(3).GetString()));
    }

    private static async Task<int> UnpaidWithSentCodesAsync(AuthApiFactory api)
    {
        using var scope = api.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ISentInvoiceQrCodeQuery>().CountUnpaidInvoicesWithSentCodesAsync(CancellationToken.None);
    }
}
