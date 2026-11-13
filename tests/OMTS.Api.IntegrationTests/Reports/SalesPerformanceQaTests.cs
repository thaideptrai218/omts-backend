using System.Net;
using ClosedXML.Excel;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Harvest;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Sales;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Reports.SalesPerformanceTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceChangeTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesReturnTestKit;

namespace OMTS.Api.IntegrationTests.Reports;

/// <summary>
/// QA probes for PR 14a (REPORT-01), written by the reviewer to close test gaps: a cancelled sales return, a cancelled invoice whose payment is kept, the drill-down of a
/// zone-limited Accountant, the file against the JSON for several groupings, and the signed total of the debts. Each test states what it expects from BR-REPORT-01.
/// </summary>
public class SalesPerformanceQaTests
{
    // CLAIM-03.3 / BR-CLAIM-03: a Cancelled return counts nowhere, not in the reports and not in the customer debt; the kg count as sold again.
    [Fact]
    public async Task A_cancelled_sales_return_counts_nowhere_in_the_figures_the_drill_down_or_the_debt()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var owner = sale.Owner.Client;
        var invoice = await sale.SellKgAsync(sale.CustomerId, 10m);
        var returned = (await owner.ReturnedAsync(invoice.Id(), ReturnBody(await owner.InvoiceAsync(invoice.Id()), "BackForSale"))).GetProperty("return");
        await (await owner.CancelSalesReturnAsync(returned.Id(), CancelBody(returned))).ReadAsync(HttpStatusCode.OK);

        var row = (await owner.ReportAsync("groupBy=customer")).Group(sale.CustomerId.ToString());
        var byProduct = await owner.ReportAsync("groupBy=product");
        var drill = await (await owner.GetGroupInvoicesAsync($"groupBy=customer&groupKey={sale.CustomerId}")).ReadAsync(HttpStatusCode.OK);

        Assert.Equal((300_000m, 0m, 300_000m, 10m, 10m), (row.Money("revenue"), row.Money("salesReturns"), row.Money("netRevenue"), row.Kg("invoicedKg"), row.Kg("soldKg")));
        Assert.Equal(300_000m, row.Money("netDebt"));
        Assert.Equal(await owner.CustomerDebtAsync(sale.CustomerId), row.Money("netDebt"));
        Assert.Equal(0m, byProduct.Group(sale.ProductId.ToString()).Money("salesReturns"));
        var line = Assert.Single(drill.GetProperty("invoices").EnumerateArray());
        Assert.Equal((0m, 300_000m), (line.Money("returnValue"), line.Money("net")));
    }

    // BR-SALE-11 / BR-DEBT-02: the cancelled invoice is in no figure, the payment it kept still lowers the debt and counts as money received.
    [Fact]
    public async Task A_cancelled_invoice_is_in_no_figure_but_its_kept_payment_still_counts_in_the_customer_money()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var owner = sale.Owner.Client;
        await sale.SellKgAsync(sale.CustomerId, 10m);
        var cancelled = await sale.SellKgAsync(sale.CustomerId, 5m, sale.OtherLotId);
        await api.RecordPaymentAsync(sale.CustomerId, 150_000m);
        await owner.CancelledAsync(cancelled.Id(), Cancellation(await owner.InvoiceAsync(cancelled.Id())));

        var row = (await owner.ReportAsync("groupBy=customer")).Group(sale.CustomerId.ToString());

        Assert.Equal((1, 300_000m, 10m), (row.GetProperty("invoiceCount").GetInt32(), row.Money("revenue"), row.Kg("invoicedKg")));
        Assert.Equal((150_000m, 150_000m), (row.Money("netMoneyReceived"), row.Money("netDebt")));
        Assert.Equal(await owner.CustomerDebtAsync(sale.CustomerId), row.Money("netDebt"));
    }

    // BR-FARM-08: the drill-down of an Accountant of zone A never shows an invoice of zone B, by zone key or by customer key; naming zone B is refused.
    [Fact]
    public async Task The_drill_down_of_a_zone_limited_accountant_shows_no_invoice_of_another_zone()
    {
        using var api = new AuthApiFactory();
        var a = await api.SaleSetupAsync();
        var b = await api.SaleSetupAsync();
        await a.SellKgAsync(a.CustomerId, 10m);
        await b.SellKgAsync(b.CustomerId, 5m);
        var accountantA = (await api.StaffAsync(await api.ZoneEntityAsync(a.ZoneId), StaffRole.Accountant)).Client;

        var byZone = await (await accountantA.GetGroupInvoicesAsync($"groupBy=zone&groupKey={b.ZoneId}")).ReadAsync(HttpStatusCode.OK);
        var byCustomer = await (await accountantA.GetGroupInvoicesAsync($"groupBy=customer&groupKey={b.CustomerId}")).ReadAsync(HttpStatusCode.OK);
        var byProduct = await (await accountantA.GetGroupInvoicesAsync($"groupBy=product&groupKey={b.ProductId}")).ReadAsync(HttpStatusCode.OK);
        var refused = await accountantA.GetGroupInvoicesAsync($"groupBy=zone&groupKey={b.ZoneId}&zoneIds={b.ZoneId}");
        var ownZone = await (await accountantA.GetGroupInvoicesAsync($"groupBy=zone&groupKey={a.ZoneId}")).ReadAsync(HttpStatusCode.OK);

        Assert.Empty(byZone.GetProperty("invoices").EnumerateArray());
        Assert.Equal("MSG-GEN-10", byZone.Text("messageCode"));
        Assert.Empty(byCustomer.GetProperty("invoices").EnumerateArray());
        Assert.Empty(byProduct.GetProperty("invoices").EnumerateArray());
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("MSG-GEN-08", await refused.ErrorCodeAsync());
        Assert.Equal(a.ZoneId, Assert.Single(ownZone.GetProperty("invoices").EnumerateArray()).GetProperty("zone").Id());
    }

    // REPORT-01.4 step 2: the file holds the figures shown. Every row and the Total row of the file equal the JSON, for the product, week and customer groupings.
    [Fact]
    public async Task The_file_rows_equal_the_json_groups_and_totals_for_every_grouping()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var owner = sale.Owner.Client;
        await sale.SellKgAsync(sale.CustomerId, 10m, discount: 20_000m, otherCharges: 5_000m);
        var cut = await sale.SellKgAsync(sale.CustomerId, 4m, sale.OtherLotId, unitPrice: 40_000m);
        await sale.CutPriceAsync(cut.Id(), 10_000m);
        await sale.SellKgAsync(null, 1m);

        foreach (var grouping in new[] { "product", "week", "customer", "zone" })
        {
            var json = await owner.ReportAsync($"groupBy={grouping}");
            var file = await owner.GetExportAsync($"groupBy={grouping}");
            Assert.Equal(HttpStatusCode.OK, file.StatusCode);
            using var book = new XLWorkbook(await file.Content.ReadAsStreamAsync());
            var sheet = book.Worksheet("Sales performance");
            var groups = json.Groups();
            for (var i = 0; i < groups.Count; i++)
            {
                AssertSameRow(groups[i], groups[i].Text("label"), sheet.Row(i + 2));
            }

            AssertSameRow(json.Totals(), "Total", sheet.Row(groups.Count + 2));
            Assert.True(sheet.Row(groups.Count + 3).IsEmpty());
        }
    }

    // PM answer 12/11: the debts at the end are split as DEBT-03 does: the receivable (debts above 0), the money owed back (debts below 0, a positive amount) and their net, so the
    // money the cooperative owes back to one customer is shown and not hidden in the receivable of the others.
    [Fact]
    public async Task The_debts_at_the_end_are_split_into_the_receivable_the_money_owed_back_and_the_net()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var owner = sale.Owner.Client;
        var other = (await owner.CreateCustomerAsync()).Id();
        await sale.SellKgAsync(sale.CustomerId, 10m);
        await sale.SellKgAsync(other, 2m, sale.OtherLotId);
        await api.RecordPaymentAsync(other, 160_000m);

        var report = await owner.ReportAsync("groupBy=customer");

        var debtor = report.Group(sale.CustomerId.ToString());
        Assert.Equal((300_000m, 0m, 300_000m), (debtor.Money("receivable"), debtor.Money("owedBack"), debtor.Money("netDebt")));
        var creditor = report.Group(other.ToString());
        Assert.Equal((0m, 100_000m, -100_000m), (creditor.Money("receivable"), creditor.Money("owedBack"), creditor.Money("netDebt")));
        var totals = report.Totals();
        Assert.Equal((300_000m, 100_000m, 200_000m), (totals.Money("receivable"), totals.Money("owedBack"), totals.Money("netDebt")));
    }

    // PM answer 12/11: money received with no customer is a row of its own, so the total of the money received matches the payments of the period; it is left out when customers
    // are chosen.
    [Fact]
    public async Task Money_received_with_no_customer_is_a_row_of_its_own_and_is_in_the_total()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var owner = sale.Owner.Client;
        var today = PurchaseRequestTestKit.Today;
        await sale.SellKgAsync(sale.CustomerId, 10m);
        await api.RecordPaymentAsync(sale.CustomerId, 100_000m, today);
        var held = await api.RecordPaymentAsync(sale.CustomerId, 80_000m, today);
        await api.UpdateAsync<OMTS.Domain.Entities.Sales.CustomerPayment>(held.Id, p => p.CustomerId = null);
        var waiting = await api.RecordPaymentAsync(sale.CustomerId, 5_000m, today, statusId: OMTS.Domain.Constants.StatusIds.CustomerPayment.PendingTransfer);
        await api.UpdateAsync<OMTS.Domain.Entities.Sales.CustomerPayment>(waiting.Id, p => p.CustomerId = null);

        var all = await owner.ReportAsync("groupBy=customer");
        var chosen = await owner.ReportAsync($"groupBy=customer&customerIds={sale.CustomerId}");

        var unassigned = all.Group("unassigned");
        Assert.Equal(("Not assigned to a customer", 0m, 0m, 80_000m), (unassigned.Text("label"), unassigned.Money("revenue"), unassigned.Money("netDebt"), unassigned.Money("netMoneyReceived")));
        Assert.Equal(100_000m, all.Group(sale.CustomerId.ToString()).Money("netMoneyReceived"));
        Assert.Equal(180_000m, all.Totals().Money("netMoneyReceived"));
        Assert.DoesNotContain(chosen.Groups(), g => g.Text("key") == "unassigned");
    }
    private static void AssertSameRow(System.Text.Json.JsonElement figures, string label, IXLRow row)
    {
        Assert.Equal(label, row.Cell(1).GetString());
        Assert.Equal((double)figures.GetProperty("invoiceCount").GetInt32(), row.Cell(2).GetDouble());
        Assert.Equal((double)figures.Kg("invoicedKg"), row.Cell(3).GetDouble());
        Assert.Equal((double)figures.Money("revenue"), row.Cell(4).GetDouble());
        Assert.Equal((double)figures.Money("priceCuts"), row.Cell(5).GetDouble());
        Assert.Equal((double)figures.Money("salesReturns"), row.Cell(6).GetDouble());
        Assert.Equal((double)figures.Money("netRevenue"), row.Cell(7).GetDouble());
        Assert.Equal((double)figures.Kg("soldKg"), row.Cell(8).GetDouble());
    }
}
