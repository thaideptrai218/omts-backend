using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Sales;
using static OMTS.Api.IntegrationTests.Reports.SalesPerformanceTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceChangeTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;

namespace OMTS.Api.IntegrationTests.Reports;

/// <summary>REPORT-02 figures (BR-REPORT-02): the weighted average, the lowest and highest price, the invoices left out and the latest unit prices.</summary>
public class PriceTrendsFigureTests
{
    // 10 kg at 30,000 and 30 kg at 20,000: (300,000 + 600,000) / 40 = 22,500, where the simple average of the two prices is 25,000. The discount and the other charges of the invoice
    // are not spread over its line (BR-REPORT-02).
    [Fact]
    public async Task The_average_is_weighted_by_kg_and_the_lowest_and_highest_are_unit_prices_whatever_the_discount()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        await sale.SellKgAsync(sale.CustomerId, 10m, unitPrice: 30_000m);
        await sale.SellKgAsync(sale.CustomerId, 30m, unitPrice: 20_000m, discount: 100_000m, otherCharges: 7_000m);

        var report = await sale.Owner.Client.PriceTrendsAsync();

        var series = Assert.Single(report.TrendSeries());
        Assert.Equal(sale.ProductId, series.GetProperty("productId").GetGuid());
        var period = Assert.Single(series.TrendPeriods());
        Assert.Equal((40m, 22_500m, 20_000m, 30_000m), period.PriceFigures());
        Assert.Equal(Day(PurchaseRequestTestKit.Today), period.Text("key"));
        Assert.Equal("day", report.Text("unit"));
        Assert.Null(report.Text("messageCode"));
    }

    [Fact]
    public async Task Cancelled_invoices_and_invoices_with_a_return_that_stands_are_left_out_and_a_cancelled_return_counts_again()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        await sale.SellKgAsync(sale.CustomerId, 10m, unitPrice: 30_000m);
        var cancelled = (await sale.SellKgAsync(sale.CustomerId, 10m, unitPrice: 90_000m)).Id();
        var returned = (await sale.SellKgAsync(sale.CustomerId, 10m, unitPrice: 5_000m)).Id();
        var returnCancelled = (await sale.SellKgAsync(sale.CustomerId, 10m, unitPrice: 40_000m)).Id();
        await sale.CancelSaleAsync(cancelled);
        await sale.ReturnAsync(returned);
        await sale.ReturnAndCancelAsync(returnCancelled);

        var report = await sale.Owner.Client.PriceTrendsAsync();

        // Only the first invoice and the one whose return was cancelled: 20 kg, from 30,000 to 40,000.
        Assert.Equal((20m, 35_000m, 30_000m, 40_000m), Assert.Single(Assert.Single(report.TrendSeries()).TrendPeriods()).PriceFigures());
    }

    // The unit price of a corrected line is its latest one (BR-SALE-10 (4)); a price cut leaves the unit prices as they were, so it is not in this report.
    [Fact]
    public async Task A_corrected_unit_price_counts_at_its_latest_value_and_a_price_cut_does_not_change_the_unit_prices()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var owner = sale.Owner.Client;
        var corrected = (await sale.SellKgAsync(sale.CustomerId, 10m, unitPrice: 30_000m)).Id();
        var cut = (await sale.SellKgAsync(sale.CustomerId, 10m, unitPrice: 30_000m)).Id();
        await owner.CorrectedAsync(corrected, DataEntry(await owner.InvoiceAsync(corrected), [Line(sale.ProductId, sale.LotId, 10m, 45_000m, priceDecidedByOwner: true)], sale.LotId));
        await sale.CutPriceAsync(cut, 50_000m);

        var period = Assert.Single(Assert.Single((await owner.PriceTrendsAsync()).TrendSeries()).TrendPeriods());

        Assert.Equal((20m, 37_500m, 30_000m, 45_000m), period.PriceFigures());
    }

    [Fact]
    public async Task Each_product_has_its_own_series_and_the_products_chosen_are_the_only_ones_shown()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var second = (await sale.Owner.Client.CreateProductAsync(unitPrice: 70_000m)).Id();
        var other = sale with { ProductId = second };
        await sale.SellKgAsync(sale.CustomerId, 10m, unitPrice: 30_000m);
        await other.SellKgAsync(sale.CustomerId, 4m, unitPrice: 70_000m);

        var all = await sale.Owner.Client.PriceTrendsAsync();
        var onlySecond = await sale.Owner.Client.PriceTrendsAsync($"productIds={second}");
        var both = await sale.Owner.Client.PriceTrendsAsync($"productIds={second}&productIds={sale.ProductId}");

        Assert.Equal(2, all.TrendSeries().Count);
        Assert.Equal(30_000m, all.TrendSeriesOf(sale.ProductId).TrendPeriods().Single().Money("averagePrice"));
        Assert.Equal(70_000m, all.TrendSeriesOf(second).TrendPeriods().Single().Money("averagePrice"));
        Assert.Equal(second, Assert.Single(onlySecond.TrendSeries()).GetProperty("productId").GetGuid());
        Assert.Equal(2, both.TrendSeries().Count);
        // The product chosen is named, as the screen and the file show it.
        Assert.Equal(second, Assert.Single(onlySecond.GetProperty("filters").GetProperty("products").EnumerateArray()).Id());
    }

    [Fact]
    public async Task No_line_in_the_selection_gives_no_series_and_msg_gen_10()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var today = PurchaseRequestTestKit.Today;
        await sale.SellKgAsync(sale.CustomerId, 10m);

        var report = await sale.Owner.Client.PriceTrendsAsync(Period(today.AddDays(-9), today.AddDays(-8)));

        Assert.Empty(report.TrendSeries());
        Assert.Equal("MSG-GEN-10", report.Text("messageCode"));
        Assert.False(string.IsNullOrEmpty(report.Text("message")));
    }
}
