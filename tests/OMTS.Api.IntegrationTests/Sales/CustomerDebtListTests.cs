using System.Net;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>DEBT-03 step 1 and the rights of §I.4.4: the Accountant (any pair, no zone) and the Owner; never the Zone Manager or the trader.</summary>
public class CustomerDebtListTests
{
    private static object PayLater(SaleSetup sale, Guid lotId, decimal kg, decimal cash = 0m) =>
        Invoice(sale.CustomerId, [Line(sale.ProductId, lotId, kg)], Cash(cash, "PayLater", payLaterOwnerAgreed: true), lotId);

    [Fact]
    public async Task The_accountant_of_any_zone_and_the_owner_view_and_export_and_the_zone_manager_and_the_trader_are_refused()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        await sale.Manager.Client.SellAsync(PayLater(sale, sale.LotId, 10m));
        var zone = await api.ZoneEntityAsync(sale.ZoneId);
        var allowed = new[] { sale.Owner.Client, (await api.StaffAsync(zone, StaffRole.Accountant)).Client, (await api.StaffAsync(null, StaffRole.Accountant)).Client };
        var refused = new[] { sale.Manager.Client, (await api.StaffAsync(null, StaffRole.ZoneManager)).Client, (await api.TraderAsync()).Client };

        foreach (var client in allowed)
        {
            Assert.Equal(300_000m, (await client.DebtListAsync()).RowOf(sale.CustomerId).Money("debt"));
            Assert.Equal(300_000m, (await client.StatementAsync(sale.CustomerId)).Money("debtToday"));
            Assert.Equal(HttpStatusCode.OK, (await client.ExportDebtsAsync(sale.CustomerId)).StatusCode);
        }

        foreach (var client in refused)
        {
            foreach (var response in new[] { await client.GetDebtsAsync(), await client.GetStatementAsync(sale.CustomerId), await client.ExportDebtsAsync() })
            {
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
                Assert.Equal("MSG-GEN-08", await response.ErrorCodeAsync());
            }
        }
    }

    // BR-DEBT-02/05: debt ≠ 0 only, the age of a debt above 0, the receivables and the money owed back apart, the payments Awaiting confirmation; oldest debt first.
    [Fact]
    public async Task The_list_shows_each_debt_with_its_age_the_totals_and_the_payments_awaiting_confirmation()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var client = sale.Manager.Client;
        var owner = sale.Owner.Client;
        var oldInvoice = (await client.SellAsync(PayLater(sale, sale.LotId, 10m, cash: 100_000m))).GetProperty("invoice");
        await api.BackDateAsync(oldInvoice.Id(), 12);
        await client.SellAsync(PayLater(sale, sale.LotId, 20m));
        var owedBack = (await owner.CreateCustomerAsync("Owed back " + AcsTestKit.NewCode())).Id();
        await client.SellAsync(Invoice(owedBack, [Line(sale.ProductId, sale.LotId, 10m)], Cash(300_000m), sale.LotId));
        await api.RecordPaymentAsync(owedBack, 50_000m);
        var settled = (await owner.CreateCustomerAsync()).Id();
        await client.SellAsync(Invoice(settled, [Line(sale.ProductId, sale.LotId, 1m)], Cash(30_000m), sale.LotId));
        var newer = (await owner.CreateCustomerAsync()).Id();
        await client.SellAsync(Invoice(newer, [Line(sale.ProductId, sale.LotId, 100m)], Cash(0m, "PayLater", payLaterOwnerAgreed: true), sale.LotId));

        var accountant = await api.StaffAsync(null, StaffRole.Accountant);
        var list = await accountant.Client.DebtListAsync();

        Assert.Equal([sale.CustomerId, newer, owedBack], list.Rows().Select(r => r.GetProperty("customer").Id()).ToList());
        var debtor = list.RowOf(sale.CustomerId);
        Assert.Equal((800_000m, 12), (debtor.Money("debt"), debtor.GetProperty("debtAgeDays").GetInt32()));
        Assert.Equal(PurchaseRequestTestKit.Today.AddDays(-12), DateOnly.Parse(debtor.Text("oldestUnpaidDate")!));
        var back = list.RowOf(owedBack);
        Assert.Equal((-50_000m, JsonValueKind.Null), (back.Money("debt"), back.GetProperty("debtAgeDays").ValueKind));
        Assert.Equal((3_800_000m, 50_000m), (list.Money("receivablesTotal"), list.Money("owedBackTotal")));
        // The cash collected at the port (three sales) is Awaiting confirmation; the transfer the Accountant records is Confirmed.
        Assert.Equal(3, list.GetProperty("awaitingConfirmationCount").GetInt32());
        Assert.Equal(3, list.GetProperty("page").GetProperty("total").GetInt32());
        Assert.Null(list.Text("messageCode"));

        var searched = await owner.DebtListAsync("?search=Owed%20back&pageSize=1");
        Assert.Equal([owedBack], searched.Rows().Select(r => r.GetProperty("customer").Id()).ToList());
        // The totals stay the cooperative's (BR-FARM-10, BR-DEBT-02), whatever the search.
        Assert.Equal((3_800_000m, 50_000m), (searched.Money("receivablesTotal"), searched.Money("owedBackTotal")));
        Assert.Equal("MSG-GEN-10", (await owner.DebtListAsync("?search=nobody-at-all")).Text("messageCode"));
    }

    // DEBT-03.1: nothing to show yet (MSG-GEN-11); a customer can still be chosen for the statement.
    [Fact]
    public async Task With_no_debt_the_list_says_there_is_nothing_to_show_yet_and_the_statement_still_opens()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        await sale.Manager.Client.SellAsync(Invoice(sale.CustomerId, [Line(sale.ProductId, sale.LotId, 10m)], Cash(300_000m), sale.LotId));

        var list = await sale.Owner.Client.DebtListAsync();

        Assert.Empty(list.Rows());
        Assert.Equal(("MSG-GEN-11", 0m, 0m), (list.Text("messageCode"), list.Money("receivablesTotal"), list.Money("owedBackTotal")));
        Assert.Equal(1, list.GetProperty("awaitingConfirmationCount").GetInt32());
        var statement = await sale.Owner.Client.StatementAsync(sale.CustomerId);
        Assert.Equal((0m, 2), (statement.Money("closingDebt"), statement.GetProperty("entries").GetArrayLength()));
        Assert.Equal(JsonValueKind.Null, statement.GetProperty("debtAgeDays").ValueKind);
    }
}
