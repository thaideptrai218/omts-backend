using System.Net;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Harvest;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Sales;
using static OMTS.Api.IntegrationTests.Reports.SalesPerformanceTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceChangeTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;

namespace OMTS.Api.IntegrationTests.Reports;

/// <summary>REPORT-01 / BR-REPORT-01: what counts, and how each figure is worked out.</summary>
public class SalesPerformanceFigureTests
{
    private static readonly string[] Figures = ["revenue", "priceCuts", "salesReturns", "netRevenue", "invoicedKg", "soldKg"];

    // The rows of the product grouping hold the line amounts, and the discounts, the other charges and the price cuts of the invoices with the same parts of their sales returns,
    // so that the rows add up to the revenue and to the net revenue.
    [Fact]
    public async Task The_product_rows_add_up_to_the_revenue_and_the_net_revenue_with_the_discount_charge_price_cut_and_return_rows()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        await sale.SellKgAsync(sale.CustomerId, 10m, discount: 20_000m, otherCharges: 5_000m);
        var returned = await sale.SellKgAsync(sale.CustomerId, 10m, sale.OtherLotId, unitPrice: 40_000m);
        await sale.CutPriceAsync(returned.Id(), 30_000m);
        await sale.ReturnAsync(returned.Id());

        var report = await sale.Owner.Client.ReportAsync("groupBy=product");

        var totals = report.Totals();
        Assert.Equal((655_000m, 30_000m, 370_000m, 285_000m), (totals.Money("revenue"), totals.Money("priceCuts"), totals.Money("salesReturns"), totals.Money("netRevenue")));
        Assert.Equal((2, 20m, 10m), (totals.GetProperty("invoiceCount").GetInt32(), totals.Kg("invoicedKg"), totals.Kg("soldKg")));
        foreach (var figure in Figures)
        {
            Assert.Equal(totals.Money(figure), report.SumOf(figure));
        }

        var product = report.Group(sale.ProductId.ToString());
        Assert.Equal((2, 20m, 700_000m, 400_000m, 10m), (product.GetProperty("invoiceCount").GetInt32(), product.Kg("invoicedKg"), product.Money("revenue"), product.Money("salesReturns"), product.Kg("soldKg")));
        Assert.Equal((-20_000m, 0m), (report.Group("discounts").Money("revenue"), report.Group("discounts").Money("salesReturns")));
        Assert.Equal(5_000m, report.Group("other-charges").Money("revenue"));
        var cuts = report.Group("price-cuts");
        Assert.Equal((-30_000m, 30_000m, -30_000m), (cuts.Money("revenue"), cuts.Money("priceCuts"), cuts.Money("salesReturns")));
        Assert.Equal("product", report.Text("groupBy"));
        Assert.Null(report.Text("messageCode"));
        // Net money received and the debt are the customer grouping's only.
        Assert.Equal(JsonValueKind.Null, product.GetProperty("netMoneyReceived").ValueKind);
    }

    [Fact]
    public async Task A_cancelled_invoice_does_not_count_and_a_corrected_invoice_counts_at_its_latest_values()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var owner = sale.Owner.Client;
        var corrected = await sale.SellKgAsync(sale.CustomerId, 10m);
        var cancelled = await sale.SellKgAsync(sale.CustomerId, 5m, sale.OtherLotId);
        await sale.SellKgAsync(sale.CustomerId, 2m);
        await owner.CorrectedAsync(corrected.Id(), DataEntry(await owner.InvoiceAsync(corrected.Id()), [Line(sale.ProductId, sale.LotId, 4m)], sale.LotId));
        await owner.CancelledAsync(cancelled.Id(), Cancellation(await owner.InvoiceAsync(cancelled.Id())));

        var totals = (await owner.ReportAsync()).Totals();

        // 4 kg (corrected from 10) and 2 kg at 30,000; the 5 kg of the cancelled invoice are nowhere.
        Assert.Equal((2, 6m, 180_000m, 6m), (totals.GetProperty("invoiceCount").GetInt32(), totals.Kg("invoicedKg"), totals.Money("revenue"), totals.Kg("soldKg")));
    }

    // BR-REPORT-01: the price cuts and the sales returns count in the period of their invoice, whatever the date of the cut or of the return (BR-REPORT-03 counts them by event date).
    [Fact]
    public async Task A_price_cut_and_a_return_made_later_count_in_the_period_of_their_invoice()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var today = PurchaseRequestTestKit.Today;
        var old = await sale.SellKgAsync(sale.CustomerId, 10m);
        await sale.SellKgAsync(sale.CustomerId, 1m, sale.OtherLotId);
        await api.DateAsync(old.Id(), today.AddDays(-40));
        await sale.CutPriceAsync(old.Id(), 10_000m);
        await sale.ReturnAsync(old.Id());

        var oldPeriod = (await sale.Owner.Client.ReportAsync(Period(today.AddDays(-45), today.AddDays(-35)))).Totals();
        var thisMonth = await sale.Owner.Client.ReportAsync(Period(today.AddDays(-10), today));

        Assert.Equal((290_000m, 10_000m, 290_000m, 0m), (oldPeriod.Money("revenue"), oldPeriod.Money("priceCuts"), oldPeriod.Money("salesReturns"), oldPeriod.Money("netRevenue")));
        // Today's period shows today's sale only: the cut and the return were made today, on an invoice of another period.
        Assert.Equal((30_000m, 0m, 0m), (thisMonth.Totals().Money("revenue"), thisMonth.Totals().Money("priceCuts"), thisMonth.Totals().Money("salesReturns")));
    }

    [Fact]
    public async Task The_invoices_of_walk_in_customers_are_one_row_of_their_own_with_no_customer_money()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        await sale.SellKgAsync(null, 2m);
        await sale.SellKgAsync(null, 3m, sale.OtherLotId);
        await sale.SellKgAsync(sale.CustomerId, 1m);

        var report = await sale.Owner.Client.ReportAsync("groupBy=customer");

        // Largest revenue first: the walk-in row (5 kg) before the customer (1 kg).
        Assert.Equal(["walk-in", sale.CustomerId.ToString()], report.Groups().Select(g => g.Text("key")));
        var walkIn = report.Group("walk-in");
        Assert.Equal(("Walk-in customers", 2, 150_000m), (walkIn.Text("label"), walkIn.GetProperty("invoiceCount").GetInt32(), walkIn.Money("revenue")));
        // The cash of a walk-in sale has no profile: it is in no customer's money.
        Assert.Equal((JsonValueKind.Null, JsonValueKind.Null), (walkIn.GetProperty("netMoneyReceived").ValueKind, walkIn.GetProperty("debtAtEnd").ValueKind));
        Assert.Equal(0m, report.Group(sale.CustomerId.ToString()).Money("netMoneyReceived"));
        Assert.Equal(180_000m, report.Totals().Money("revenue"));
    }

    [Fact]
    public async Task With_no_invoice_in_the_selection_the_answer_is_msg_gen_10_and_the_totals_are_zero()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var cancelled = await sale.SellKgAsync(sale.CustomerId, 5m);
        await sale.Owner.Client.CancelledAsync(cancelled.Id(), Cancellation(await sale.Owner.Client.InvoiceAsync(cancelled.Id())));

        var report = await sale.Owner.Client.ReportAsync();

        Assert.Equal("MSG-GEN-10", report.Text("messageCode"));
        Assert.Empty(report.Groups());
        Assert.Equal((0m, 0m, 0m), (report.Totals().Money("revenue"), report.Totals().Money("netRevenue"), report.Totals().Money("netMoneyReceived")));
        Assert.Equal(0, report.Totals().GetProperty("invoiceCount").GetInt32());
    }

    // The default is the current month and the grouping by customer; the answer echoes the period and the choices.
    [Fact]
    public async Task The_default_is_the_current_month_by_customer_and_the_answer_echoes_the_choices()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        await sale.SellKgAsync(sale.CustomerId, 1m);
        var today = PurchaseRequestTestKit.Today;

        var report = await sale.Owner.Client.ReportAsync($"zoneIds={sale.ZoneId}&customerIds={sale.CustomerId}&productIds={sale.ProductId}");

        Assert.Equal(("customer", new DateOnly(today.Year, today.Month, 1), new DateOnly(today.Year, today.Month, 1).AddMonths(1).AddDays(-1)),
            (report.Text("groupBy"), DateOnly.Parse(report.Text("from")), DateOnly.Parse(report.Text("to"))));
        Assert.True(DateTimeOffset.TryParse(report.Text("computedAt"), out _));
        var filters = report.GetProperty("filters");
        Assert.Equal(sale.ZoneId, Assert.Single(filters.GetProperty("zones").EnumerateArray()).Id());
        Assert.Equal(sale.CustomerId, Assert.Single(filters.GetProperty("customers").EnumerateArray()).Id());
        Assert.Equal(sale.ProductId, Assert.Single(filters.GetProperty("products").EnumerateArray()).Id());
        Assert.Equal(30_000m, report.Totals().Money("revenue"));
    }

    // Product filter: the invoices that have the product on a line count whole, so every grouping adds up to the same totals.
    [Fact]
    public async Task A_product_filter_keeps_the_invoices_that_have_the_product()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var other = (await sale.Owner.Client.CreateProductAsync(unitPrice: 50_000m)).Id();
        await sale.SellKgAsync(sale.CustomerId, 2m);
        var body = Invoice(sale.CustomerId, [Line(other, sale.OtherLotId, 1m)], Cash(0m, "PayLater", payLaterOwnerAgreed: true), sale.OtherLotId);
        await sale.Owner.Client.SellAsync(body);

        var onlyOther = await sale.Owner.Client.ReportAsync($"productIds={other}");
        var both = await sale.Owner.Client.ReportAsync();

        Assert.Equal((1, 50_000m), (onlyOther.Totals().GetProperty("invoiceCount").GetInt32(), onlyOther.Totals().Money("revenue")));
        Assert.Equal((2, 110_000m), (both.Totals().GetProperty("invoiceCount").GetInt32(), both.Totals().Money("revenue")));
    }
}
