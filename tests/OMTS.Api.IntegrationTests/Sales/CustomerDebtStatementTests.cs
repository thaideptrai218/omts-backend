using System.Net;
using System.Text.Json;
using ClosedXML.Excel;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>DEBT-03 step 3 (BR-DEBT-04/05), DEBT-03.2 export, and one debt everywhere: list, statement, customer view and purchase requests, merged profiles together.</summary>
public class CustomerDebtStatementTests
{
    private static DateOnly Today => PurchaseRequestTestKit.Today;

    private static object PayLater(Guid customerId, SaleSetup sale, decimal kg, decimal cash = 0m) =>
        Invoice(customerId, [Line(sale.ProductId, sale.LotId, kg)], Cash(cash, "PayLater", payLaterOwnerAgreed: true), sale.LotId);

    [Fact]
    public async Task The_statement_runs_from_the_opening_debt_to_the_closing_debt_and_lists_the_unpaid_invoices_oldest_first()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var client = sale.Manager.Client;
        var first = (await client.SellAsync(PayLater(sale.CustomerId, sale, 10m))).GetProperty("invoice");
        await api.BackDateAsync(first.Id(), 20);
        var second = (await client.SellAsync(PayLater(sale.CustomerId, sale, 20m, cash: 100_000m))).GetProperty("invoice");
        await api.BackDateAsync(second.Id(), 5);
        var transfer = await api.RecordPaymentAsync(sale.CustomerId, 200_000m, Today.AddDays(-3));
        var cancelled = await api.RecordPaymentAsync(sale.CustomerId, 999_000m, Today.AddDays(-2), statusId: StatusIds.CustomerPayment.Cancelled);

        var cashCode = Assert.Single(await api.PaymentsOfAsync(second.Id())).Code;

        var statement = await sale.Owner.Client.StatementAsync(sale.CustomerId, Today.AddDays(-10), Today);

        Assert.Equal((300_000m, 600_000m, 600_000m), (statement.Money("openingDebt"), statement.Money("closingDebt"), statement.Money("debtToday")));
        Assert.Equal(
            [
                ("Invoice", second.Text("code"), 600_000m, 900_000m, false, false),
                ("Payment", cashCode, -100_000m, 800_000m, true, false),
                ("Payment", transfer.Code, -200_000m, 600_000m, false, false),
                ("Payment", cancelled.Code, 0m, 600_000m, false, true),
            ],
            statement.GetProperty("entries").EnumerateArray().Select(e => (
                e.Text("kind"), e.Text("code"), e.Money("debtChange"), e.Money("debtAfter"),
                e.GetProperty("awaitingConfirmation").GetBoolean(), e.GetProperty("cancelled").GetBoolean())).ToList());
        var cash = statement.GetProperty("entries")[1];
        Assert.Equal((second.Text("code"), "Cash", 100_000m), (cash.Text("invoiceCode"), cash.Text("method"), cash.Money("amount")));

        // BR-DEBT-05: the transfer covers the oldest invoice; the cash came with the second one. Σ unpaid = debt, aged from the first invoice.
        Assert.Equal([(first.Text("code"), 100_000m), (second.Text("code"), 500_000m)], statement.Unpaid());
        Assert.Equal(20, statement.GetProperty("debtAgeDays").GetInt32());

        // The period defaults to the current month; a start after the end is MSG-GEN-01; an unknown profile 404.
        var month = await sale.Owner.Client.StatementAsync(sale.CustomerId);
        Assert.Equal((new DateOnly(Today.Year, Today.Month, 1), new DateOnly(Today.Year, Today.Month, 1).AddMonths(1).AddDays(-1)),
            (DateOnly.Parse(month.Text("from")!), DateOnly.Parse(month.Text("to")!)));
        Assert.Equal("MSG-GEN-01", await (await sale.Owner.Client.GetStatementAsync(sale.CustomerId, Today, Today.AddDays(-1))).ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await sale.Owner.Client.GetStatementAsync(Guid.NewGuid())).StatusCode);
    }

    // SALE-04.1, SCR-SALE-02 and DEBT-03 read one figure; after SALE-04.3 the merged profile's invoices and payments are allocated with the kept profile's.
    [Fact]
    public async Task The_debt_is_the_same_in_the_list_the_statement_the_customer_view_and_the_purchase_requests_also_after_a_merge()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var trader = await api.TraderAsync();
        var request = await trader.Client.PlaceAsync(sale.ProductId, 50m);
        var client = sale.Manager.Client;
        var keptOld = (await client.SellAsync(PayLater(trader.Profile.Id, sale, 10m))).GetProperty("invoice");
        await api.BackDateAsync(keptOld.Id(), 9);
        var mergedInvoice = (await client.SellAsync(PayLater(sale.CustomerId, sale, 20m))).GetProperty("invoice");
        await api.BackDateAsync(mergedInvoice.Id(), 4);
        await api.RecordPaymentAsync(sale.CustomerId, 400_000m);

        Assert.Equal(200_000m, (await sale.Owner.Client.DebtListAsync()).RowOf(sale.CustomerId).Money("debt"));
        (await sale.Owner.Client.MergeAsync(trader.Profile.Id, sale.CustomerId, ownerApproved: true)).EnsureSuccessStatusCode();

        var list = await sale.Owner.Client.DebtListAsync();
        var statement = await sale.Owner.Client.StatementAsync(trader.Profile.Id, Today.AddDays(-30), Today);
        var views = new[]
        {
            list.RowOf(trader.Profile.Id).Money("debt"),
            statement.Money("debtToday"),
            statement.Money("closingDebt"),
            statement.Unpaid().Sum(u => u.Unpaid),
            await client.CustomerDebtAsync(trader.Profile.Id),
            (await sale.Owner.Client.ViewedByStaffAsync(request.Id())).GetProperty("customer").Money("currentDebt"),
        };
        Assert.All(views, v => Assert.Equal(500_000m, v));
        Assert.Single(list.Rows());
        // The transfer of the merged profile now covers the oldest invoice of the kept one first.
        Assert.Equal([(mergedInvoice.Text("code"), 500_000m)], statement.Unpaid());
        Assert.Equal(0m, await client.CustomerDebtAsync(sale.CustomerId));
    }

    // DEBT-03.2: the list, the statement shown and its unpaid invoices; without a customer only the list. A wrong period gives no file.
    [Fact]
    public async Task The_export_holds_the_list_the_statement_and_the_unpaid_invoices_that_are_shown()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var invoice = (await sale.Manager.Client.SellAsync(PayLater(sale.CustomerId, sale, 10m, cash: 50_000m))).GetProperty("invoice");
        var accountant = await api.StaffAsync(null, OMTS.Domain.Enums.StaffRole.Accountant);
        var statement = await accountant.Client.StatementAsync(sale.CustomerId);

        var file = await accountant.Client.ExportDebtsAsync(sale.CustomerId);

        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", file.Content.Headers.ContentType?.MediaType);
        using var workbook = new XLWorkbook(await file.Content.ReadAsStreamAsync());
        var debts = workbook.Worksheet("Customer debts").RowsUsed().Skip(1).ToList();
        Assert.Equal((250_000m, "Total receivables", 250_000m), (debts[0].Cell(3).GetValue<decimal>(), debts[1].Cell(1).GetString(), debts[1].Cell(3).GetValue<decimal>()));
        var lines = workbook.Worksheet("Statement").RowsUsed().Skip(1).ToList();
        Assert.Equal(statement.GetProperty("entries").GetArrayLength() + 2, lines.Count);
        Assert.Equal(("Sales invoice", invoice.Text("code"), "Awaiting confirmation"), (lines[1].Cell(2).GetString(), lines[1].Cell(3).GetString(), lines[2].Cell(6).GetString()));
        Assert.Equal(250_000m, lines[^1].Cell(8).GetValue<decimal>());
        var unpaid = Assert.Single(workbook.Worksheet("Unpaid invoices").RowsUsed().Skip(1));
        Assert.Equal((invoice.Text("code"), 250_000m), (unpaid.Cell(1).GetString(), unpaid.Cell(5).GetValue<decimal>()));

        using var listOnly = new XLWorkbook(await (await accountant.Client.ExportDebtsAsync()).Content.ReadAsStreamAsync());
        Assert.Equal(["Customer debts"], listOnly.Worksheets.Select(w => w.Name).ToList());
        Assert.Equal("MSG-GEN-01", await (await accountant.Client.ExportDebtsAsync(sale.CustomerId, Today, Today.AddDays(-1))).ErrorCodeAsync());
    }
}
