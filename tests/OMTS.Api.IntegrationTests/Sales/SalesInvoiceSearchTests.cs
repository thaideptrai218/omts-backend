using System.Net;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Sales.PurchaseRequestTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceSearchTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>SALE-09 steps 1-3: today by default, each filter, the total of every match, the rights.</summary>
public class SalesInvoiceSearchTests
{
    [Fact]
    public async Task By_default_the_list_is_today_in_the_zones_of_the_caller_with_the_total_of_the_completed_invoices()
    {
        using var api = new AuthApiFactory();
        var a = await api.SaleSetupAsync();
        var b = await api.SaleSetupAsync();
        var cash = await a.Manager.Client.SoldAsync(a, a.LotId);
        var later = (await a.Manager.Client.SellAsync(a.PayLater(a.LotId, 20m))).GetProperty("invoice");
        var old = await a.Manager.Client.SoldAsync(a, a.LotId);
        await api.BackDateAsync(old.Id(), 3);
        var other = await b.Manager.Client.SoldAsync(b, b.LotId, 5m);

        var mine = await a.Manager.Client.InvoiceListAsync();
        Assert.Equal([later.Text("code"), cash.Text("code")], mine.Codes());
        Assert.Equal((2, 900_000m), (mine.GetProperty("page").GetProperty("total").GetInt32(), mine.Money("completedTotal")));
        Assert.Equal((Today, Today), (DateOnly.Parse(mine.Text("from")!), DateOnly.Parse(mine.Text("to")!)));

        var row = mine.Rows()[0];
        Assert.Equal((later.Id(), a.ZoneId, a.CustomerId), (row.Id(), row.GetProperty("zone").Id(), row.GetProperty("customer").Id()));
        Assert.Equal((600_000m, "PayLater", false, 600_000m), (row.Money("total"), row.Text("paymentTerm"), row.GetProperty("isPaid").GetBoolean(), row.Money("unpaidAmount")));
        Assert.Equal((StatusIds.SalesInvoice.Completed, StatusIds.MessageDelivery.NotSent), (row.GetProperty("statusId").GetInt32(), row.GetProperty("deliveryStatusId").GetInt32()));
        Assert.True(mine.Rows()[1].GetProperty("isPaid").GetBoolean());
        Assert.Equal(JsonNull, row.GetProperty("cashAwaitingConfirmation").ValueKind);

        var all = await a.Owner.Client.InvoiceListAsync();
        Assert.Equal(new[] { other.Text("code"), later.Text("code"), cash.Text("code") }.Order(), all.Codes().Order());
        Assert.Equal(1_050_000m, all.Money("completedTotal"));
    }

    private const System.Text.Json.JsonValueKind JsonNull = System.Text.Json.JsonValueKind.Null;

    // BR-SALE-08: the zone of the invoice decides who sees it.
    [Fact]
    public async Task A_zone_manager_sees_only_the_invoices_of_their_zone_and_every_other_role_of_their_pairs()
    {
        using var api = new AuthApiFactory();
        var a = await api.SaleSetupAsync();
        var b = await api.SaleSetupAsync();
        var inA = await a.Manager.Client.SoldAsync(a, a.LotId);
        var inB = await b.Manager.Client.SoldAsync(b, b.LotId);
        var zoneA = await api.ZoneEntityAsync(a.ZoneId);
        var accountantA = (await api.StaffAsync(zoneA, StaffRole.Accountant)).Client;
        var accountantAll = (await api.StaffAsync(null, StaffRole.Accountant)).Client;
        var managerAll = (await api.StaffAsync(null, StaffRole.ZoneManager)).Client;

        Assert.Equal([inA.Text("code")], (await a.Manager.Client.InvoiceListAsync()).Codes());
        Assert.Equal([inA.Text("code")], (await accountantA.InvoiceListAsync()).Codes());
        Assert.Equal(2, (await accountantAll.InvoiceListAsync()).Rows().Count);
        Assert.Equal(2, (await managerAll.InvoiceListAsync()).Rows().Count);

        foreach (var response in new[]
                 {
                     await a.Manager.Client.GetInvoicesAsync($"?zoneId={b.ZoneId}"), await a.Manager.Client.GetInvoiceAsync(inB.Id()),
                     await accountantA.GetInvoicesAsync($"?zoneId={b.ZoneId}"), await accountantA.GetInvoiceAsync(inB.Id()),
                 })
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("MSG-GEN-08", await response.ErrorCodeAsync());
        }

        Assert.Equal([inA.Text("code")], (await a.Manager.Client.InvoiceListAsync($"?zoneId={a.ZoneId}")).Codes());
        Assert.Equal(HttpStatusCode.OK, (await accountantA.GetInvoiceAsync(inA.Id())).StatusCode);
    }

    [Fact]
    public async Task A_trader_may_not_search_or_open_or_export_invoices()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var invoice = await sale.Manager.Client.SoldAsync(sale, sale.LotId);
        var trader = (await api.TraderAsync()).Client;

        foreach (var response in new[] { await trader.GetInvoicesAsync(), await trader.GetInvoiceAsync(invoice.Id()), await trader.ExportInvoicesAsync(), await trader.PrintInvoiceAsync(invoice.Id()) })
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("MSG-GEN-08", await response.ErrorCodeAsync());
        }
    }

    // SALE-09 step 3: each filter, alone.
    [Fact]
    public async Task Each_filter_narrows_the_list()
    {
        using var api = new AuthApiFactory();
        var a = await api.SaleSetupAsync();
        var b = await api.SaleSetupAsync();
        var manager = a.Manager.Client;
        var other = await a.Owner.Client.CreateCustomerAsync();
        var first = await manager.SoldAsync(a, a.LotId);
        var second = await manager.SoldAsync(a, a.OtherLotId, customerId: other.Id());
        var unpaid = (await manager.SellAsync(a.PayLater(a.OtherLotId, 1m))).GetProperty("invoice");
        // Walk-in: no customer debt, so its cash kept after the cancellation (BR-SALE-11) covers nothing else.
        var cancelled = await manager.SoldAsync(a, a.LotId, walkIn: true);
        await api.UpdateAsync<SalesInvoice>(cancelled.Id(), i => i.StatusId = StatusIds.SalesInvoice.Cancelled);
        var inB = await b.Manager.Client.SoldAsync(b, b.LotId);
        var old = await manager.SoldAsync(a, a.LotId);
        await api.BackDateAsync(old.Id(), 40);
        var owner = a.Owner.Client;

        Assert.Equal([inB.Text("code")], (await owner.InvoiceListAsync(Query(("zoneId", b.ZoneId)))).Codes());
        Assert.Equal([second.Text("code")], (await manager.InvoiceListAsync(Query(("customerId", other.Id())))).Codes());
        Assert.Equal(new[] { unpaid.Text("code"), second.Text("code") }.Order(), (await manager.InvoiceListAsync(Query(("harvestLotId", a.OtherLotId)))).Codes().Order());
        Assert.Equal([inB.Text("code")], (await owner.InvoiceListAsync(Query(("raftId", b.RaftId)))).Codes());
        Assert.Equal([cancelled.Text("code")], (await manager.InvoiceListAsync(Query(("statusId", StatusIds.SalesInvoice.Cancelled)))).Codes());
        Assert.Equal([unpaid.Text("code")], (await manager.InvoiceListAsync(Query(("paid", false)))).Codes());
        Assert.Equal(new[] { second.Text("code"), first.Text("code") }.Order(), (await manager.InvoiceListAsync(Query(("paid", true)))).Codes().Order());
        Assert.Equal([first.Text("code")], (await manager.InvoiceListAsync(Query(("code", first.Text("code")![2..])))).Codes());
        Assert.Equal([old.Text("code")], (await manager.InvoiceListAsync(Query(("from", Today.AddDays(-45)), ("to", Today.AddDays(-30))))).Codes());
    }

    // BR-DEBT-05: paid is what the allocation covers, oldest first: the customer's transfer pays the older invoice.
    [Fact]
    public async Task The_paid_filter_follows_the_debt_allocation_and_a_walk_in_invoice_its_own_payments()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var manager = sale.Manager.Client;
        var older = (await manager.SellAsync(sale.PayLater(sale.LotId, 10m))).GetProperty("invoice");
        await api.BackDateAsync(older.Id(), 2);
        var newer = (await manager.SellAsync(sale.PayLater(sale.LotId, 10m))).GetProperty("invoice");
        await api.RecordPaymentAsync(sale.CustomerId, 300_000m);
        var walkIn = await manager.SoldAsync(sale, sale.LotId, walkIn: true);
        var span = Query(("from", Today.AddDays(-5)), ("to", Today));

        var paid = await manager.InvoiceListAsync(span + "&paid=true");
        var notPaid = await manager.InvoiceListAsync(span + "&paid=false");

        Assert.Equal(new[] { older.Text("code"), walkIn.Text("code") }.Order(), paid.Codes().Order());
        Assert.Equal((600_000m, 2), (paid.Money("completedTotal"), paid.GetProperty("page").GetProperty("total").GetInt32()));
        Assert.Equal([newer.Text("code")], notPaid.Codes());
        var row = notPaid.Rows()[0];
        Assert.Equal((false, 300_000m), (row.GetProperty("isPaid").GetBoolean(), row.Money("unpaidAmount")));
    }

    // SALE-09 step 1: the total counts every match, not the page.
    [Fact]
    public async Task The_total_counts_the_completed_invoices_of_every_page_and_the_cancelled_ones_count_nothing()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var manager = sale.Manager.Client;
        await manager.SoldAsync(sale, sale.LotId, 1m);
        await manager.SoldAsync(sale, sale.LotId, 2m);
        var third = await manager.SoldAsync(sale, sale.LotId, 3m);
        await api.UpdateAsync<SalesInvoice>(third.Id(), i => i.StatusId = StatusIds.SalesInvoice.Cancelled);

        var page1 = await manager.InvoiceListAsync("?pageSize=1&page=1");
        var page2 = await manager.InvoiceListAsync("?pageSize=1&page=2");
        var beyond = await manager.InvoiceListAsync("?pageSize=1&page=9");

        Assert.Equal((1, 3), (page1.Rows().Count, page1.GetProperty("page").GetProperty("total").GetInt32()));
        Assert.Equal(90_000m, page1.Money("completedTotal"));
        Assert.Equal(90_000m, page2.Money("completedTotal"));
        Assert.NotEqual(page1.Codes(), page2.Codes());
        Assert.Empty(beyond.Rows());
        Assert.Equal(90_000m, beyond.Money("completedTotal"));
    }

    // SALE-09 step 3 and SALE-09.0.E1/E2.
    [Fact]
    public async Task The_cash_filter_shows_the_cash_this_caller_collected_and_not_yet_confirmed()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var manager = sale.Manager.Client;
        var other = (await api.ManagerAsync(sale.ZoneId)).Client;
        var mine = await manager.SoldAsync(sale, sale.LotId);
        var confirmed = await manager.SoldAsync(sale, sale.LotId);
        var byOther = await other.SoldAsync(sale, sale.LotId);
        var payLater = (await manager.SellAsync(sale.PayLater(sale.LotId, 1m))).GetProperty("invoice");
        var payment = (await api.PaymentsOfAsync(confirmed.Id())).Single();
        await api.UpdateAsync<CustomerPayment>(payment.Id, p => p.StatusId = StatusIds.CustomerPayment.Completed);

        var list = await manager.InvoiceListAsync("?cashCollectedByMe=true");

        Assert.Equal([mine.Text("code")], list.Codes());
        Assert.Equal(300_000m, list.Rows()[0].Money("cashAwaitingConfirmation"));
        Assert.Equal([byOther.Text("code")], (await other.InvoiceListAsync("?cashCollectedByMe=true")).Codes());
        Assert.Empty((await sale.Owner.Client.InvoiceListAsync("?cashCollectedByMe=true")).Rows());
        Assert.Contains(payLater.Text("code"), (await manager.InvoiceListAsync()).Codes());
    }

    [Fact]
    public async Task A_wrong_period_is_refused_and_nothing_found_answers_msg_gen_10()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        await sale.Manager.Client.SoldAsync(sale, sale.LotId);
        var client = sale.Manager.Client;

        var wrong = await client.GetInvoicesAsync(Query(("from", Today), ("to", Today.AddDays(-1))));
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Equal("MSG-GEN-01", await wrong.ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetInvoicesAsync(Query(("from", Today.AddYears(-6)), ("to", Today)))).StatusCode);

        var empty = await client.InvoiceListAsync(Query(("from", Today.AddDays(-9)), ("to", Today.AddDays(-8))));
        Assert.Equal(("MSG-GEN-10", 0, 0m), (empty.Text("messageCode"), empty.GetProperty("page").GetProperty("total").GetInt32(), empty.Money("completedTotal")));
        Assert.NotNull(empty.Text("message"));
    }

    [Fact]
    public async Task A_filter_that_finds_nothing_answers_msg_gen_10_and_part_of_a_code_is_plain_text()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var sold = await sale.Manager.Client.SoldAsync(sale, sale.LotId);
        var client = sale.Manager.Client;

        foreach (var text in new[] { "HD%", "HD_", "[H]D" })
        {
            var list = await client.InvoiceListAsync(Query(("code", text)));
            Assert.Equal(("MSG-GEN-10", 0), (list.Text("messageCode"), list.Rows().Count));
        }

        Assert.Equal([sold.Text("code")], (await client.InvoiceListAsync(Query(("code", " " + sold.Text("code")![..3] + " ")))).Codes());
    }
}
