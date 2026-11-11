using System.Net;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Sales;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Reports.PostSaleIssuesTestKit;
using static OMTS.Api.IntegrationTests.Reports.SalesPerformanceTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesReturnTestKit;

namespace OMTS.Api.IntegrationTests.Reports;

/// <summary>REPORT-03 grouped by customer, zone and harvest lot, the events of a group, and the zones of the user (BR-FARM-08).</summary>
public class PostSaleIssuesGroupingTests
{
    // Zone A (the trader buys): invoice 1 of lot 1 (6 kg) and lot 2 (4 kg) with a claim and a return (lot 1 back for sale, lot 2 thrown away); invoice 2 of lot 1 (10 kg) with a price cut of
    // 20,000; invoice 3 of lot 2 (4 kg) cancelled (120,000). Zone B: a price cut of 5,000 for another customer.
    private sealed record World(AuthApiFactory Api, SaleSetup A, SaleSetup B, TraderActor Trader, Guid First, Guid Second, Guid Third, Guid Fourth);

    private static async Task<World> WorldAsync()
    {
        var api = new AuthApiFactory();
        var a = await api.SaleSetupAsync();
        var b = await api.SaleSetupAsync();
        var trader = await PurchaseRequestTestKit.TraderAsync(api);
        var first = (await a.SellTwoLotsAsync(trader.Profile.Id)).Id();
        var second = (await a.SellKgAsync(trader.Profile.Id, 10m)).Id();
        var third = (await a.SellKgAsync(trader.Profile.Id, 4m, a.OtherLotId)).Id();
        var fourth = (await b.SellKgAsync(b.CustomerId, 10m)).Id();
        await trader.SendClaimAsync(first);
        var invoice = await a.Owner.Client.InvoiceAsync(first);
        // Lot 2 gets thrown-away kg back, so the return answers MSG-HARVEST-02: lot 2 stays for sale.
        await a.Owner.Client.ReturnedAsync(
            first, ReturnBody(invoice, "BackForSale", "ThrownAway").With("thrownAwayLots", new[] { new { harvestLotId = a.OtherLotId, stillForSale = true } }));
        await a.CutPriceAsync(second, 20_000m);
        await a.CancelSaleAsync(third);
        await b.CutPriceAsync(fourth, 5_000m);
        return new World(api, a, b, trader, first, second, third, fourth);
    }

    [Fact]
    public async Task Grouped_by_customer_each_customer_has_the_events_of_its_invoices()
    {
        var w = await WorldAsync();
        using var _ = w.Api;

        var report = await w.A.Owner.Client.PostSaleAsync();

        var trader = report.Group(w.Trader.Profile.Id.ToString());
        Assert.Equal((1, 1, 1, 1), trader.EventCounts());
        Assert.Equal((1, 0, 0), (trader.GetProperty("claimsAwaiting").GetInt32(), trader.GetProperty("claimsRejected").GetInt32(), trader.GetProperty("claimsAccepted").GetInt32()));
        Assert.Equal((10m, 4m, 300_000m, 20_000m, 120_000m), (trader.Money("returnedKg"), trader.Money("discardedKg"), trader.Money("returnsValue"), trader.Money("priceCutsAmount"), trader.Money("cancelledTotal")));
        Assert.Equal((0, 0, 1, 0), report.Group(w.B.CustomerId.ToString()).EventCounts());
        Assert.Equal((1, 1, 2, 1), report.Totals().EventCounts());
        Assert.Equal(25_000m, report.Totals().Money("priceCutsAmount"));
        Assert.Equal("customer", report.Text("groupBy"));
    }

    [Fact]
    public async Task Grouped_by_zone_each_event_is_in_the_zone_of_its_invoice_and_the_zone_filter_keeps_one()
    {
        var w = await WorldAsync();
        using var _ = w.Api;

        var report = await w.A.Owner.Client.PostSaleAsync("groupBy=zone");
        var onlyB = await w.A.Owner.Client.PostSaleAsync($"groupBy=zone&zoneIds={w.B.ZoneId}");

        Assert.Equal((1, 1, 1, 1), report.Group(w.A.ZoneId.ToString()).EventCounts());
        Assert.Equal((0, 0, 1, 0), report.Group(w.B.ZoneId.ToString()).EventCounts());
        Assert.Equal((await w.Api.ZoneEntityAsync(w.A.ZoneId)).Name, report.Group(w.A.ZoneId.ToString()).Text("label"));
        Assert.Equal((0, 0, 1, 0), Assert.Single(onlyB.Groups()).EventCounts());
        Assert.Equal((0, 0, 1, 0), onlyB.Totals().EventCounts());
    }

    // A claim, a price cut and a cancellation are of a whole invoice that may sell several lots, so by lot they are null; the lot has the kg of its own return line.
    [Fact]
    public async Task Grouped_by_lot_the_returned_and_discarded_kg_are_those_of_the_lot_and_whole_invoice_figures_are_not_split()
    {
        var w = await WorldAsync();
        using var _ = w.Api;

        var report = await w.A.Owner.Client.PostSaleAsync("groupBy=lot");

        var (one, two) = (report.Group(w.A.LotId.ToString()), report.Group(w.A.OtherLotId.ToString()));
        Assert.Equal((await w.Api.LotOfAsync(w.A.LotId)).LotCode, one.Text("label"));
        Assert.Equal((1, 6m, 0m), (one.GetProperty("returnsCount").GetInt32(), one.Money("returnedKg"), one.Money("discardedKg")));
        Assert.Equal((1, 4m, 4m), (two.GetProperty("returnsCount").GetInt32(), two.Money("returnedKg"), two.Money("discardedKg")));
        Assert.Equal(2, report.Groups().Count);
        foreach (var figure in new[] { "claimsSent", "claimsAwaiting", "claimsRejected", "claimsAccepted", "returnsValue", "priceCutsCount", "priceCutsAmount", "cancelledCount", "cancelledTotal" })
        {
            Assert.Equal(JsonValueKind.Null, one.GetProperty(figure).ValueKind);
            Assert.Equal(JsonValueKind.Null, two.GetProperty(figure).ValueKind);
        }

        // The total counts each event once: the return of two lots is one return, and the whole-invoice figures are there.
        Assert.Equal((1, 1, 2, 1), report.Totals().EventCounts());
        Assert.Equal((10m, 4m, 300_000m), (report.Totals().Money("returnedKg"), report.Totals().Money("discardedKg"), report.Totals().Money("returnsValue")));
    }

    [Fact]
    public async Task The_customer_filter_keeps_the_customers_chosen()
    {
        var w = await WorldAsync();
        using var _ = w.Api;

        var report = await w.A.Owner.Client.PostSaleAsync($"customerIds={w.B.CustomerId}");

        Assert.Equal(w.B.CustomerId.ToString(), Assert.Single(report.Groups()).Text("key"));
        Assert.Equal((0, 0, 1, 0), report.Totals().EventCounts());
        Assert.Equal(w.B.CustomerId, Assert.Single(report.GetProperty("filters").GetProperty("customers").EnumerateArray()).Id());
    }

    [Fact]
    public async Task The_events_of_a_group_are_listed_with_their_type_invoice_customer_note_and_amount()
    {
        var w = await WorldAsync();
        using var _ = w.Api;
        var owner = w.A.Owner.Client;
        var invoice = await owner.InvoiceAsync(w.First);

        var byCustomer = await (await owner.GetPostSaleItemsAsync($"groupBy=customer&groupKey={w.Trader.Profile.Id}")).ReadAsync(HttpStatusCode.OK);
        var items = byCustomer.PostSaleItems();

        Assert.Equal(["cancellation", "claim", "priceCut", "return"], items.Select(i => i.Text("type")).Order(StringComparer.Ordinal));
        var salesReturn = items.Single(i => i.Text("type") == "return");
        Assert.Equal((w.First, invoice.Text("code"), 300_000m, ReturnReason), (salesReturn.GetProperty("invoiceId").GetGuid(), salesReturn.Text("invoiceCode"), salesReturn.Money("amount"), salesReturn.Text("noteOrReason")));
        Assert.Equal(w.Trader.Profile.Id, salesReturn.GetProperty("customer").Id());
        Assert.Equal(QualityClaimTestKit.Description, items.Single(i => i.Text("type") == "claim").Text("noteOrReason"));
        Assert.Equal("awaitingResponse", items.Single(i => i.Text("type") == "claim").Text("status"));
        Assert.Equal(JsonValueKind.Null, items.Single(i => i.Text("type") == "claim").GetProperty("amount").ValueKind);
        Assert.Equal(20_000m, items.Single(i => i.Text("type") == "priceCut").Money("amount"));
        Assert.Equal(120_000m, items.Single(i => i.Text("type") == "cancellation").Money("amount"));
        Assert.Equal(PurchaseRequestTestKit.Today, DateOnly.Parse(salesReturn.Text("date")));

        // By lot: the issues of the invoices that sell it, which are not split between lots (the invoice of two lots is in both).
        var lotOne = (await (await owner.GetPostSaleItemsAsync($"groupBy=lot&groupKey={w.A.LotId}")).ReadAsync(HttpStatusCode.OK)).PostSaleItems();
        var lotTwo = (await (await owner.GetPostSaleItemsAsync($"groupBy=lot&groupKey={w.A.OtherLotId}")).ReadAsync(HttpStatusCode.OK)).PostSaleItems();
        Assert.Equal(["claim", "priceCut", "return"], lotOne.Select(i => i.Text("type")).Order(StringComparer.Ordinal));
        Assert.Equal(["cancellation", "claim", "return"], lotTwo.Select(i => i.Text("type")).Order(StringComparer.Ordinal));

        var byZone = (await (await owner.GetPostSaleItemsAsync($"groupBy=zone&groupKey={w.B.ZoneId}")).ReadAsync(HttpStatusCode.OK)).PostSaleItems();
        Assert.Equal("priceCut", Assert.Single(byZone).Text("type"));

        var none = await (await owner.GetPostSaleItemsAsync($"groupBy=customer&groupKey={Guid.NewGuid()}")).ReadAsync(HttpStatusCode.OK);
        Assert.Empty(none.PostSaleItems());
        Assert.Equal("MSG-GEN-10", none.Text("messageCode"));
    }

    [Fact]
    public async Task A_user_of_one_zone_sees_that_zone_only_and_a_zone_outside_theirs_is_refused()
    {
        var w = await WorldAsync();
        using var _ = w.Api;
        var accountantA = (await w.Api.StaffAsync(await w.Api.ZoneEntityAsync(w.A.ZoneId), StaffRole.Accountant)).Client;

        var own = await accountantA.PostSaleAsync("groupBy=zone");
        var ownItems = await (await accountantA.GetPostSaleItemsAsync($"groupBy=zone&groupKey={w.B.ZoneId}")).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(w.A.ZoneId.ToString(), Assert.Single(own.Groups()).Text("key"));
        Assert.Equal((1, 1, 1, 1), own.Totals().EventCounts());
        // The invoices of the other zone are not read, so its group has no event.
        Assert.Empty(ownItems.PostSaleItems());
        foreach (var query in new[] { $"zoneIds={w.B.ZoneId}", $"zoneIds={w.A.ZoneId}&zoneIds={w.B.ZoneId}" })
        {
            var refused = await accountantA.GetPostSaleAsync(query);
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Equal("MSG-GEN-08", await refused.ErrorCodeAsync());
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await accountantA.GetPostSaleExportAsync($"zoneIds={w.B.ZoneId}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await accountantA.GetPostSaleExportAsync($"zoneIds={w.A.ZoneId}")).StatusCode);
    }
}
