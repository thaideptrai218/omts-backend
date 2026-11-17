using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Sales;
using OMTS.Domain.Constants;
using static OMTS.Api.IntegrationTests.Reports.PostSaleIssuesTestKit;
using static OMTS.Api.IntegrationTests.Reports.SalesPerformanceTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceChangeTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;

namespace OMTS.Api.IntegrationTests.Reports;

/// <summary>REPORT-03 figures (BR-REPORT-03): every event counted by its own date, what does not count, and the claim statuses.</summary>
public class PostSaleIssuesFigureTests
{
    // The invoices are of last month; the price cut, the return and the cancellation are made now: they are this month's issues, where REPORT-01 counts them in the month of their invoice.
    [Fact]
    public async Task A_price_cut_a_return_and_a_cancellation_are_counted_in_the_month_of_the_event_not_of_the_invoice()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var lastMonth = PurchaseRequestTestKit.Today.AddMonths(-1);
        var cut = (await sale.SellKgAsync(sale.CustomerId, 10m)).Id();
        var returned = (await sale.SellKgAsync(sale.CustomerId, 10m)).Id();
        var cancelled = (await sale.SellKgAsync(sale.CustomerId, 4m)).Id();
        foreach (var id in new[] { cut, returned, cancelled })
        {
            await api.DateAsync(id, lastMonth);
        }

        await sale.CutPriceAsync(cut, 20_000m);
        await sale.ReturnAsync(returned);
        await sale.CancelSaleAsync(cancelled);

        var thisMonth = await sale.Owner.Client.PostSaleAsync();
        var group = Assert.Single(thisMonth.Groups());
        Assert.Equal(sale.CustomerId.ToString(), group.Text("key"));
        Assert.Equal((0, 1, 1, 1), group.EventCounts());
        Assert.Equal((10m, 0m, 300_000m, 20_000m, 120_000m), (group.Money("returnedKg"), group.Money("discardedKg"), group.Money("returnsValue"), group.Money("priceCutsAmount"), group.Money("cancelledTotal")));
        Assert.Equal(thisMonth.Totals().Money("cancelledTotal"), group.Money("cancelledTotal"));
        Assert.Null(thisMonth.Text("messageCode"));

        // Last month, where the invoices are, nothing happened yet; the sales report counts nothing this month for them.
        var firstOfLastMonth = new DateOnly(lastMonth.Year, lastMonth.Month, 1);
        var before = await sale.Owner.Client.PostSaleAsync(Period(firstOfLastMonth, firstOfLastMonth.AddMonths(1).AddDays(-1)));
        Assert.Empty(before.Groups());
        Assert.Equal("MSG-GEN-10", before.Text("messageCode"));
        Assert.Equal("MSG-GEN-10", (await sale.Owner.Client.ReportAsync()).Text("messageCode"));
    }

    // 2026-10-31 at 23:59 and 2026-11-01 at 00:00 in Vietnam (UTC+7): the same instants as 16:59 and 17:00 on the 31st in UTC. One of each event on each side of midnight.
    [Fact]
    public async Task Each_event_is_counted_in_the_business_day_of_its_own_date_at_the_edge_of_the_period()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var trader = await PurchaseRequestTestKit.TraderAsync(api);
        var buyer = trader.Profile.Id;
        var (october, november) = (new DateOnly(2026, 10, 31), new DateOnly(2026, 11, 1));
        var beforeMidnight = new DateTimeOffset(2026, 10, 31, 23, 59, 0, TimeSpan.FromHours(7));
        var afterMidnight = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.FromHours(7));
        var cuts = new[] { (await sale.SellKgAsync(buyer, 1m)).Id(), (await sale.SellKgAsync(buyer, 1m)).Id() };
        var returns = new[] { (await sale.SellKgAsync(buyer, 1m)).Id(), (await sale.SellKgAsync(buyer, 1m)).Id() };
        var cancels = new[] { (await sale.SellKgAsync(buyer, 1m)).Id(), (await sale.SellKgAsync(buyer, 1m)).Id() };
        var claims = new[] { await trader.SendClaimAsync(cuts[0]), await trader.SendClaimAsync(cuts[1]) };

        foreach (var (id, date) in new[] { (cuts[0], october), (cuts[1], november) })
        {
            await sale.CutPriceAsync(id, 1_000m);
            await api.MovePriceCutsAsync(id, date);
        }

        foreach (var (id, date) in new[] { (returns[0], october), (returns[1], november) })
        {
            await api.MoveReturnAsync(await sale.ReturnAsync(id), date);
        }

        foreach (var (id, at) in new[] { (cancels[0], beforeMidnight), (cancels[1], afterMidnight) })
        {
            await sale.CancelSaleAsync(id);
            await api.MoveCancellationAsync(id, at);
        }

        await api.MoveClaimAsync(claims[0], beforeMidnight);
        await api.MoveClaimAsync(claims[1], afterMidnight);

        var inOctober = await sale.Owner.Client.PostSaleAsync(Period(new DateOnly(2026, 10, 1), october));
        var inNovember = await sale.Owner.Client.PostSaleAsync(Period(november, new DateOnly(2026, 11, 30)));

        Assert.Equal((1, 1, 1, 1), Assert.Single(inOctober.Groups()).EventCounts());
        Assert.Equal((1, 1, 1, 1), Assert.Single(inNovember.Groups()).EventCounts());
        Assert.Equal((1, 1, 1, 1), inOctober.Totals().EventCounts());
        Assert.Equal(1_000m, inNovember.Totals().Money("priceCutsAmount"));
    }

    [Fact]
    public async Task A_cancelled_return_and_a_correction_of_a_data_entry_error_are_not_post_sale_issues()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var owner = sale.Owner.Client;
        var returnCancelled = (await sale.SellKgAsync(sale.CustomerId, 10m)).Id();
        var corrected = (await sale.SellKgAsync(sale.CustomerId, 10m)).Id();
        await sale.ReturnAndCancelAsync(returnCancelled);
        await owner.CorrectedAsync(corrected, DataEntry(await owner.InvoiceAsync(corrected), [Line(sale.ProductId, sale.LotId, 9m, ListPrice)], sale.LotId));

        var report = await owner.PostSaleAsync();

        Assert.Empty(report.Groups());
        Assert.Equal("MSG-GEN-10", report.Text("messageCode"));
        Assert.Equal((0, 0, 0, 0), report.Totals().EventCounts());

        // A real price cut on the corrected invoice is the only event.
        await sale.CutPriceAsync(corrected, 5_000m);
        var withCut = await owner.PostSaleAsync();
        Assert.Equal((0, 0, 1, 0), Assert.Single(withCut.Groups()).EventCounts());
        Assert.Equal(5_000m, withCut.Totals().Money("priceCutsAmount"));
    }

    // PM answer 12/11: a claim of an invoice that is cancelled later still counts, but the price cut is undone with the invoice: its amount is not an event, the cancellation is.
    [Fact]
    public async Task The_price_cut_of_a_cancelled_invoice_is_dropped_but_its_claim_and_its_cancellation_count()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var trader = await PurchaseRequestTestKit.TraderAsync(api);
        var invoice = (await sale.SellKgAsync(trader.Profile.Id, 10m)).Id();
        await trader.SendClaimAsync(invoice);
        await sale.CutPriceAsync(invoice, 20_000m);
        var kept = (await sale.SellKgAsync(trader.Profile.Id, 4m)).Id();
        await sale.CutPriceAsync(kept, 5_000m);
        await sale.CancelSaleAsync(invoice);

        var report = await sale.Owner.Client.PostSaleAsync();

        var group = Assert.Single(report.Groups());
        Assert.Equal((1, 0, 1, 1), group.EventCounts());
        Assert.Equal(5_000m, group.Money("priceCutsAmount"));
    }

    // Four claims: Awaiting response, Rejected, Accepted, and one sent last month that this month does not count.
    [Fact]
    public async Task Claims_are_counted_by_the_time_they_were_sent_with_their_status()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var trader = await PurchaseRequestTestKit.TraderAsync(api);
        var invoices = new List<Guid>();
        for (var i = 0; i < 4; i++)
        {
            invoices.Add((await sale.SellKgAsync(trader.Profile.Id, 1m)).Id());
        }

        var claims = new List<Guid>();
        foreach (var invoice in invoices)
        {
            claims.Add(await trader.SendClaimAsync(invoice));
        }

        await api.MoveClaimAsync(claims[1], NoonOf(PurchaseRequestTestKit.Today), StatusIds.QualityClaim.Rejected);
        await api.MoveClaimAsync(claims[2], NoonOf(PurchaseRequestTestKit.Today), StatusIds.QualityClaim.Accepted);
        await api.MoveClaimAsync(claims[3], NoonOf(PurchaseRequestTestKit.Today.AddMonths(-1)), StatusIds.QualityClaim.Accepted);

        var group = Assert.Single((await sale.Owner.Client.PostSaleAsync()).Groups());

        Assert.Equal((3, 1, 1, 1), (group.GetProperty("claimsSent").GetInt32(), group.GetProperty("claimsAwaiting").GetInt32(), group.GetProperty("claimsRejected").GetInt32(), group.GetProperty("claimsAccepted").GetInt32()));
        Assert.Equal(0, group.GetProperty("returnsCount").GetInt32());
    }

    [Fact]
    public async Task No_event_in_the_selection_gives_no_group_zero_totals_and_msg_gen_10()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        await sale.SellKgAsync(sale.CustomerId, 10m);

        var report = await sale.Owner.Client.PostSaleAsync();

        Assert.Empty(report.Groups());
        Assert.Equal("MSG-GEN-10", report.Text("messageCode"));
        Assert.Equal(0m, report.Totals().Money("cancelledTotal"));
        Assert.Equal("customer", report.Text("groupBy"));
    }
}
