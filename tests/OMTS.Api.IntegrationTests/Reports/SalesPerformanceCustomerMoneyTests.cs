using System.Net;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Sales;
using OMTS.Domain.Constants;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Reports.SalesPerformanceTestKit;
using static OMTS.Api.IntegrationTests.Sales.CustomerDebtTestKit;

namespace OMTS.Api.IntegrationTests.Reports;

/// <summary>REPORT-01 grouped by customer: the net money received in the period and the customer debt at its end (BR-DEBT-02, BR-DEBT-03), and the invoices of a group (REPORT-01.3).</summary>
public class SalesPerformanceCustomerMoneyTests
{
    // Period [today-10, today-1]. The customer owes: invoices of 1,000,000 (20 days ago, zone A), 200,000 (5 days ago, zone A) and 100,000 (4 days ago, zone B).
    // Payments: 400,000 Confirmed 15 days ago (before the period), 300,000 Confirmed 5 days ago, 100,000 Awaiting confirmation 3 days ago, 50,000 paid back 2 days ago,
    // 999,999 cancelled, 70,000 Confirmed today (after the period).
    // Net money received = 300,000 - 50,000 (Confirmed only, by payment date, minus money paid back). Debt at the end = 1,300,000 - (400,000 + 300,000 + 100,000) + 50,000 = 550,000.
    [Fact]
    public async Task The_net_money_received_counts_confirmed_payments_minus_money_paid_back_in_the_period_and_the_debt_counts_every_payment_up_to_its_end()
    {
        using var api = new AuthApiFactory();
        var a = await api.SaleSetupAsync();
        var b = await api.SaleSetupAsync();
        var today = PurchaseRequestTestKit.Today;
        var customer = a.CustomerId;
        await api.DateAsync((await a.SellKgAsync(customer, 40m, unitPrice: 25_000m)).Id(), today.AddDays(-20));
        await api.DateAsync((await a.SellKgAsync(customer, 8m, a.OtherLotId, 25_000m)).Id(), today.AddDays(-5));
        await api.DateAsync((await b.SellKgAsync(customer, 4m, unitPrice: 25_000m)).Id(), today.AddDays(-4));
        await api.RecordPaymentAsync(customer, 400_000m, today.AddDays(-15));
        await api.RecordPaymentAsync(customer, 300_000m, today.AddDays(-5));
        await api.RecordPaymentAsync(customer, 100_000m, today.AddDays(-3), statusId: StatusIds.CustomerPayment.AwaitingConfirmation);
        await api.RecordPaymentAsync(customer, 50_000m, today.AddDays(-2), CustomerPaymentKind.PaidBack);
        await api.RecordPaymentAsync(customer, 999_999m, today.AddDays(-6), statusId: StatusIds.CustomerPayment.Cancelled);
        await api.RecordPaymentAsync(customer, 70_000m, today);
        var period = Period(today.AddDays(-10), today.AddDays(-1));

        var all = (await a.Owner.Client.ReportAsync($"{period}&groupBy=customer")).Group(customer.ToString());
        var zoneA = await a.Owner.Client.ReportAsync($"{period}&groupBy=customer&zoneIds={a.ZoneId}");

        // The invoices follow the zone filter; the money does not (payments and debts have no zone, BR-FARM-08).
        Assert.Equal((300_000m, 250_000m, 550_000m), (all.Money("revenue"), all.Money("netMoneyReceived"), all.Money("netDebt")));
        var inZoneA = zoneA.Group(customer.ToString());
        Assert.Equal((200_000m, 250_000m, 550_000m), (inZoneA.Money("revenue"), inZoneA.Money("netMoneyReceived"), inZoneA.Money("netDebt")));
        Assert.Equal((250_000m, 550_000m), (zoneA.Totals().Money("netMoneyReceived"), zoneA.Totals().Money("netDebt")));
        // The debt now (today) differs: it also counts the payment of today and what is dated after the period.
        Assert.Equal(480_000m, await a.Owner.Client.CustomerDebtAsync(customer));
    }

    // The sales return counts in the period of its invoice, whatever its date (BR-REPORT-01); the debt at the end counts the returns dated up to that end (BR-DEBT-02, as the statement).
    [Fact]
    public async Task The_return_counts_in_the_period_of_its_invoice_but_the_debt_at_the_end_counts_it_only_from_its_date()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var today = PurchaseRequestTestKit.Today;
        var invoice = await sale.SellKgAsync(sale.CustomerId, 10m);
        await api.DateAsync(invoice.Id(), today.AddDays(-3));
        await sale.ReturnAsync(invoice.Id());

        var beforeReturn = (await sale.Owner.Client.ReportAsync($"{Period(today.AddDays(-5), today.AddDays(-1))}&groupBy=customer")).Group(sale.CustomerId.ToString());
        var afterReturn = (await sale.Owner.Client.ReportAsync($"{Period(today.AddDays(-5), today)}&groupBy=customer")).Group(sale.CustomerId.ToString());

        Assert.Equal((300_000m, 300_000m, 300_000m), (beforeReturn.Money("revenue"), beforeReturn.Money("salesReturns"), beforeReturn.Money("netDebt")));
        Assert.Equal((300_000m, 300_000m, 0m), (afterReturn.Money("revenue"), afterReturn.Money("salesReturns"), afterReturn.Money("netDebt")));
    }

    // REPORT-01.3: the invoices of a group with their code, date, customer, zone, total, value of the sales return and the net amount.
    [Fact]
    public async Task The_invoices_of_a_group_show_the_total_the_return_and_the_net_amount()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var kept = await sale.SellKgAsync(sale.CustomerId, 10m, discount: 20_000m);
        var returned = await sale.SellKgAsync(sale.CustomerId, 5m, sale.OtherLotId);
        await sale.SellKgAsync(null, 1m);
        await sale.ReturnAsync(returned.Id());
        var owner = sale.Owner.Client;

        var byCustomer = await (await owner.GetGroupInvoicesAsync($"groupBy=customer&groupKey={sale.CustomerId}")).ReadAsync(HttpStatusCode.OK);
        var walkIn = await (await owner.GetGroupInvoicesAsync("groupBy=customer&groupKey=walk-in")).ReadAsync(HttpStatusCode.OK);
        var discounts = await (await owner.GetGroupInvoicesAsync("groupBy=product&groupKey=discounts")).ReadAsync(HttpStatusCode.OK);

        var rows = byCustomer.GetProperty("invoices").EnumerateArray().ToList();
        Assert.Equal([returned.Id(), kept.Id()], rows.Select(r => r.Id()).ToList());
        var row = rows[0];
        Assert.Equal((returned.Text("code"), 150_000m, 150_000m, 0m), (row.Text("code"), row.Money("total"), row.Money("returnValue"), row.Money("net")));
        Assert.Equal((280_000m, 0m, 280_000m), (rows[1].Money("total"), rows[1].Money("returnValue"), rows[1].Money("net")));
        Assert.Equal(sale.CustomerId, row.GetProperty("customer").Id());
        Assert.Equal(sale.ZoneId, row.GetProperty("zone").Id());
        Assert.Equal(PurchaseRequestTestKit.Today, DateOnly.Parse(row.Text("date")));
        Assert.Null(byCustomer.Text("messageCode"));
        Assert.Equal(System.Text.Json.JsonValueKind.Null, Assert.Single(walkIn.GetProperty("invoices").EnumerateArray()).GetProperty("customer").ValueKind);
        Assert.Equal([kept.Id()], discounts.GetProperty("invoices").EnumerateArray().Select(r => r.Id()).ToList());
    }

    [Fact]
    public async Task A_group_with_no_invoice_answers_msg_gen_10_and_a_missing_key_is_msg_gen_01()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        await sale.SellKgAsync(sale.CustomerId, 1m);

        var none = await (await sale.Owner.Client.GetGroupInvoicesAsync($"groupBy=customer&groupKey={Guid.NewGuid()}")).ReadAsync(HttpStatusCode.OK);
        var missing = await sale.Owner.Client.GetGroupInvoicesAsync("groupBy=customer");

        Assert.Equal("MSG-GEN-10", none.Text("messageCode"));
        Assert.Empty(none.GetProperty("invoices").EnumerateArray());
        Assert.Equal(["Group key"], await missing.InvalidFieldsAsync());
    }
}
