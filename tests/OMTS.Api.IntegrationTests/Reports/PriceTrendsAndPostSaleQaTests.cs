using System.Net;
using System.Text.Json;
using ClosedXML.Excel;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Sales;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Reports.PostSaleIssuesTestKit;
using static OMTS.Api.IntegrationTests.Reports.SalesPerformanceTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceChangeTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesReturnTestKit;

namespace OMTS.Api.IntegrationTests.Reports;

/// <summary>
/// QA probes of PR 14b (REPORT-02, REPORT-03). P1 is the BR-REPORT-03 reading of QA finding H1 (fixed); the others pin the current behaviour (P4 and P6 pin choices the PM may change).
/// </summary>
public class PriceTrendsAndPostSaleQaTests
{
    private static readonly string[] PostSaleFigures =
    [
        "claimsSent", "claimsAwaiting", "claimsRejected", "claimsAccepted", "returnsCount", "returnedKg", "discardedKg", "returnsValue", "priceCutsCount", "priceCutsAmount",
        "cancelledCount", "cancelledTotal",
    ];

    // P1 (H1). Lot 1 is sold by an invoice with a claim and by an invoice with a price cut; lot 2 by an invoice that is cancelled. No return.
    // BR-REPORT-03 "by harvest lot: the issues of the invoices that sell the lot": both lots must be rows (whole-invoice amounts empty), so the drill-down can be opened.
    [Fact]
    public async Task Qa_P1_by_lot_a_lot_whose_invoices_have_only_a_claim_a_price_cut_or_a_cancellation_is_still_a_row()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var trader = await PurchaseRequestTestKit.TraderAsync(api);
        var claimed = (await sale.SellKgAsync(trader.Profile.Id, 5m)).Id();
        var cut = (await sale.SellKgAsync(trader.Profile.Id, 5m)).Id();
        var cancelled = (await sale.SellKgAsync(trader.Profile.Id, 5m, sale.OtherLotId)).Id();
        await trader.SendClaimAsync(claimed);
        await sale.CutPriceAsync(cut, 10_000m);
        await sale.CancelSaleAsync(cancelled);
        var owner = sale.Owner.Client;

        var byLot = await owner.PostSaleAsync("groupBy=lot");
        var lotOneItems = (await (await owner.GetPostSaleItemsAsync($"groupBy=lot&groupKey={sale.LotId}")).ReadAsync(HttpStatusCode.OK)).PostSaleItems();

        // The drill-down of lot 1 lists its issues, and the totals count them.
        Assert.Equal(["claim", "priceCut"], lotOneItems.Select(i => i.Text("type")).Order(StringComparer.Ordinal));
        Assert.Equal((1, 0, 1, 1), byLot.Totals().EventCounts());

        // Both lots are rows, so their drill-down can be opened from the report.
        var keys = byLot.Groups().Select(g => g.Text("key")).ToList();
        Assert.Contains(sale.LotId.ToString(), keys);
        Assert.Contains(sale.OtherLotId.ToString(), keys);
    }

    // P2. Customers have no zone, so the same customer and product buy in zone A and in zone B. An Accountant of zone A sees nothing of zone B by any key, and the file names zone A.
    [Fact]
    public async Task Qa_P2_a_zone_limited_accountant_gets_nothing_of_another_zone_by_any_drill_down_key_and_the_file_names_their_zone()
    {
        using var api = new AuthApiFactory();
        var a = await api.SaleSetupAsync();
        var b = await api.SaleSetupAsync();
        var inA = (await a.SellKgAsync(a.CustomerId, 10m, unitPrice: 30_000m)).Id();
        var inB = (await (b with { ProductId = a.ProductId }).SellKgAsync(a.CustomerId, 10m, unitPrice: 50_000m)).Id();
        await a.CutPriceAsync(inA, 1_000m);
        await b.CutPriceAsync(inB, 7_000m);
        var accountantA = (await api.StaffAsync(await api.ZoneEntityAsync(a.ZoneId), StaffRole.Accountant)).Client;

        var report = await accountantA.PostSaleAsync();
        Assert.Equal(1_000m, Assert.Single(report.Groups()).Money("priceCutsAmount"));
        Assert.Equal(1_000m, report.Totals().Money("priceCutsAmount"));

        var byCustomer = (await (await accountantA.GetPostSaleItemsAsync($"groupBy=customer&groupKey={a.CustomerId}")).ReadAsync(HttpStatusCode.OK)).PostSaleItems();
        Assert.Equal(inA, Assert.Single(byCustomer).GetProperty("invoiceId").GetGuid());

        var byLotOfB = await (await accountantA.GetPostSaleItemsAsync($"groupBy=lot&groupKey={b.LotId}")).ReadAsync(HttpStatusCode.OK);
        Assert.Empty(byLotOfB.PostSaleItems());
        Assert.Equal("MSG-GEN-10", byLotOfB.Text("messageCode"));

        // No zone chosen by a user of zone A only: the Choices sheet names zone A, not "All" (the REPORT-01 fix in ReportFilterResolver).
        foreach (var file in new[] { await accountantA.GetPostSaleExportAsync(), await accountantA.GetPriceTrendsExportAsync() })
        {
            Assert.Equal(HttpStatusCode.OK, file.StatusCode);
            using var book = new XLWorkbook(await file.Content.ReadAsStreamAsync());
            var choices = book.Worksheet("Choices").RowsUsed().Skip(1).ToDictionary(r => r.Cell(1).GetString(), r => r.Cell(2).GetString());
            Assert.Equal((await api.ZoneEntityAsync(a.ZoneId)).Name, choices["Zones"]);
        }

        // REPORT-02.2: the lines of the product sold in both zones are those of zone A only.
        var lines = (await (await accountantA.GetPriceTrendLinesAsync($"productId={a.ProductId}&periodKey={Day(PurchaseRequestTestKit.Today)}")).ReadAsync(HttpStatusCode.OK)).TrendLines();
        Assert.Equal(inA, Assert.Single(lines).GetProperty("invoiceId").GetGuid());
    }

    // P3. Every row and every figure of the two files equal the JSON: REPORT-02 by month, REPORT-03 by customer, zone and lot (empty cell for a null figure, Total row last).
    [Fact]
    public async Task Qa_P3_every_row_of_the_files_equals_the_json_for_every_grouping()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var owner = sale.Owner.Client;
        var trader = await PurchaseRequestTestKit.TraderAsync(api);
        var second = (await owner.CreateProductAsync(unitPrice: 70_000m)).Id();
        var first = (await sale.SellTwoLotsAsync(trader.Profile.Id)).Id();
        await (sale with { ProductId = second }).SellKgAsync(trader.Profile.Id, 3m, unitPrice: 71_000m);
        var cut = (await sale.SellKgAsync(sale.CustomerId, 2.5m, unitPrice: 33_333m)).Id();
        await sale.SellKgAsync(sale.CustomerId, 7m, unitPrice: 31_000m);
        await trader.SendClaimAsync(first);
        // Lot 2 gets thrown-away kg, so the return answers MSG-HARVEST-02: lot 2 stays for sale.
        await owner.ReturnedAsync(
            first, ReturnBody(await owner.InvoiceAsync(first), "BackForSale", "ThrownAway").With("thrownAwayLots", new[] { new { harvestLotId = sale.OtherLotId, stillForSale = true } }));
        await sale.CutPriceAsync(cut, 3_000m);

        var trends = await owner.PriceTrendsAsync("unit=month");
        var expected = trends.TrendSeries()
            .SelectMany(s => s.TrendPeriods().Select(p => (Product: s.Text("productName"), Label: p.Text("label"), Figures: p.PriceFigures())))
            .ToList();
        Assert.Equal(2, expected.Count);
        using (var book = new XLWorkbook(await (await owner.GetPriceTrendsExportAsync("unit=month")).Content.ReadAsStreamAsync()))
        {
            var sheet = book.Worksheet("Price trends");
            for (var i = 0; i < expected.Count; i++)
            {
                var row = sheet.Row(i + 2);
                Assert.Equal((expected[i].Product, expected[i].Label), (row.Cell(1).GetString(), row.Cell(2).GetString()));
                Assert.Equal((double)expected[i].Figures.SoldKg, row.Cell(3).GetDouble(), 6);
                Assert.Equal((double)expected[i].Figures.Average, row.Cell(4).GetDouble(), 6);
                Assert.Equal((double)expected[i].Figures.Lowest, row.Cell(5).GetDouble(), 6);
                Assert.Equal((double)expected[i].Figures.Highest, row.Cell(6).GetDouble(), 6);
            }

            Assert.True(sheet.Cell(expected.Count + 2, 1).IsEmpty());
        }

        foreach (var grouping in new[] { "customer", "zone", "lot" })
        {
            var json = await owner.PostSaleAsync($"groupBy={grouping}");
            var rows = json.Groups().Append(json.Totals()).ToList();
            Assert.True(rows.Count >= 2, grouping);
            using var book = new XLWorkbook(await (await owner.GetPostSaleExportAsync($"groupBy={grouping}")).Content.ReadAsStreamAsync());
            var sheet = book.Worksheet("Post-sale issues");
            for (var i = 0; i < rows.Count; i++)
            {
                var row = sheet.Row(i + 2);
                Assert.Equal(rows[i].Text("label"), row.Cell(1).GetString());
                for (var c = 0; c < PostSaleFigures.Length; c++)
                {
                    var value = rows[i].GetProperty(PostSaleFigures[c]);
                    var cell = row.Cell(c + 2);
                    if (value.ValueKind == JsonValueKind.Null)
                    {
                        Assert.True(cell.IsEmpty(), $"{grouping} row {i} {PostSaleFigures[c]} should be empty");
                    }
                    else
                    {
                        Assert.Equal((double)value.GetDecimal(), cell.GetDouble(), 6);
                    }
                }
            }

            Assert.True(sheet.Cell(rows.Count + 2, 1).IsEmpty());
        }
    }

    // P4 (pins, PM). 0.333 kg at 10,001: the stored amount is 3,330 (kg x price to the dong), so the average is 3,330 / 0.333 = 10,000, below the lowest unit price 10,001 (finding L1).
    [Fact]
    public async Task Qa_P4_pins_that_the_average_of_rounded_amounts_can_fall_below_the_lowest_unit_price()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        await sale.SellKgAsync(sale.CustomerId, 0.333m, unitPrice: 10_001m);

        var period = Assert.Single(Assert.Single((await sale.Owner.Client.PriceTrendsAsync()).TrendSeries()).TrendPeriods());

        Assert.Equal((0.333m, 10_000m, 10_001m, 10_001m), period.PriceFigures());
    }

    // P5. A claim on an invoice cancelled later counts, with the cancellation (coder default 8); a cancelled return is in no grouping, no total and no drill-down (BR-CLAIM-03).
    [Fact]
    public async Task Qa_P5_a_claim_on_an_invoice_cancelled_later_counts_with_the_cancellation_and_a_cancelled_return_is_nowhere()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var trader = await PurchaseRequestTestKit.TraderAsync(api);
        var claimedThenCancelled = (await sale.SellKgAsync(trader.Profile.Id, 4m)).Id();
        var returnCancelled = (await sale.SellKgAsync(trader.Profile.Id, 6m, sale.OtherLotId)).Id();
        await trader.SendClaimAsync(claimedThenCancelled);
        await sale.CancelSaleAsync(claimedThenCancelled);
        await sale.ReturnAndCancelAsync(returnCancelled);
        var owner = sale.Owner.Client;

        var byCustomer = await owner.PostSaleAsync();
        Assert.Equal((1, 0, 0, 1), byCustomer.Group(trader.Profile.Id.ToString()).EventCounts());
        Assert.Equal(120_000m, byCustomer.Totals().Money("cancelledTotal"));
        Assert.Equal(0m, byCustomer.Totals().Money("returnsValue"));

        var byLot = await owner.PostSaleAsync("groupBy=lot");
        Assert.DoesNotContain(byLot.Groups(), g => g.Text("key") == sale.OtherLotId.ToString());
        Assert.Equal((0, 0m), (byLot.Totals().GetProperty("returnsCount").GetInt32(), byLot.Totals().Money("returnedKg")));
        var otherLot = await (await owner.GetPostSaleItemsAsync($"groupBy=lot&groupKey={sale.OtherLotId}")).ReadAsync(HttpStatusCode.OK);
        Assert.Empty(otherLot.PostSaleItems());
    }

    // P6 (pins, finding L3). A period key outside the period and malformed group keys answer 200 with MSG-GEN-10, not 400 MSG-GEN-01 "Period key" / "Group key".
    [Fact]
    public async Task Qa_P6_pins_that_a_period_key_outside_the_period_and_a_malformed_group_key_give_an_empty_200()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        await sale.SellKgAsync(sale.CustomerId, 1m);
        var owner = sale.Owner.Client;
        var today = PurchaseRequestTestKit.Today;

        var outside = await (await owner.GetPriceTrendLinesAsync($"{Period(today, today)}&productId={sale.ProductId}&periodKey={Day(today.AddYears(-3))}")).ReadAsync(HttpStatusCode.OK);
        Assert.Empty(outside.TrendLines());
        Assert.Equal("MSG-GEN-10", outside.Text("messageCode"));

        foreach (var query in new[] { "groupBy=lot&groupKey=not-a-lot", "groupBy=customer&groupKey=x", "groupBy=zone&groupKey=total" })
        {
            var none = await (await owner.GetPostSaleItemsAsync(query)).ReadAsync(HttpStatusCode.OK);
            Assert.Empty(none.PostSaleItems());
            Assert.Equal("MSG-GEN-10", none.Text("messageCode"));
        }
    }

    // P7. A data-entry correction that moves a line to another product (SALE-07) counts at its latest product, kg and unit price (BR-REPORT-02 "at their latest unit prices").
    [Fact]
    public async Task Qa_P7_a_line_moved_to_another_product_by_a_correction_counts_for_its_latest_product_kg_and_price()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var owner = sale.Owner.Client;
        var second = (await owner.CreateProductAsync(unitPrice: 70_000m)).Id();
        var moved = (await sale.SellKgAsync(sale.CustomerId, 10m, unitPrice: 30_000m)).Id();
        await sale.SellKgAsync(sale.CustomerId, 5m, unitPrice: 20_000m);
        await owner.CorrectedAsync(moved, DataEntry(await owner.InvoiceAsync(moved), [Line(second, sale.LotId, 8m, 25_000m, priceDecidedByOwner: true)], sale.LotId));

        var report = await owner.PriceTrendsAsync();

        Assert.Equal((5m, 20_000m, 20_000m, 20_000m), report.TrendSeriesOf(sale.ProductId).TrendPeriods().Single().PriceFigures());
        Assert.Equal((8m, 25_000m, 25_000m, 25_000m), report.TrendSeriesOf(second).TrendPeriods().Single().PriceFigures());
        // The post-sale report has no event: a data-entry correction is not a post-sale issue.
        Assert.Equal("MSG-GEN-10", (await owner.PostSaleAsync()).Text("messageCode"));
    }
}
