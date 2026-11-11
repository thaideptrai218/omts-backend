using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Reports.SalesPerformance;

namespace OMTS.Application.UnitTests.Features.Reports;

/// <summary>BR-REPORT-01 as a pure function: the figures of each grouping, and the rows of the product grouping adding up to the revenue and the net revenue.</summary>
public class SalesPerformanceCalculatorTests
{
    private static readonly Guid ZoneA = Guid.NewGuid();
    private static readonly Guid ZoneB = Guid.NewGuid();
    private static readonly Guid Oyster = Guid.NewGuid();
    private static readonly Guid Other = Guid.NewGuid();
    private static readonly Guid Buyer = Guid.NewGuid();

    // Invoice 1: 10 kg oyster (300,000) and 5 kg other (100,000), discount 20,000, other charges 5,000, price cut 30,000: total 355,000; not returned.
    // Invoice 2: 10 kg oyster (300,000), returned in full: total 300,000, return value 300,000.
    // Invoice 3: walk-in, zone B, 2 kg oyster (60,000).
    private static readonly SalesPerformanceInvoice One = new(
        Guid.NewGuid(), "HD000001", new DateOnly(2026, 11, 1), ZoneA, "KV01", "Zone A", Buyer, "Buyer", 400_000m, 20_000m, 5_000m, 30_000m, 355_000m, 0m, false);

    private static readonly SalesPerformanceInvoice Two = new(
        Guid.NewGuid(), "HD000002", new DateOnly(2026, 11, 2), ZoneA, "KV01", "Zone A", Buyer, "Buyer", 300_000m, 0m, 0m, 0m, 300_000m, 300_000m, true);

    private static readonly SalesPerformanceInvoice Three = new(
        Guid.NewGuid(), "HD000003", new DateOnly(2026, 11, 2), ZoneB, "KV02", "Zone B", null, null, 60_000m, 0m, 0m, 0m, 60_000m, 0m, false);

    private static readonly SalesPerformanceFacts Facts = new(
        [One, Two, Three],
        [
            new(One.Id, Oyster, "Oyster", 10m, 300_000m, 0m),
            new(One.Id, Other, "Other", 5m, 100_000m, 0m),
            new(Two.Id, Oyster, "Oyster", 10m, 300_000m, 10m),
            new(Three.Id, Oyster, "Oyster", 2m, 60_000m, 0m),
        ]);

    [Fact]
    public void The_totals_are_the_figures_of_the_invoices()
    {
        var totals = SalesPerformanceCalculator.Build(Facts, SalesPerformanceGrouping.Customer).Totals;

        // Revenue is the invoice totals; net revenue is revenue minus the sales returns; sold kg is invoiced kg minus returned kg.
        Assert.Equal((3, 27m, 715_000m, 30_000m, 300_000m, 415_000m, 17m), (totals.InvoiceCount, totals.InvoicedKg, totals.Revenue, totals.PriceCuts, totals.SalesReturns, totals.NetRevenue, totals.SoldKg));
        Assert.Null(totals.NetMoneyReceived);
    }

    [Fact]
    public void Grouped_by_customer_the_walk_in_invoices_have_a_row_of_their_own()
    {
        var groups = SalesPerformanceCalculator.Build(Facts, SalesPerformanceGrouping.Customer).Groups;

        Assert.Equal([Buyer.ToString(), SalesPerformanceCalculator.WalkInKey], groups.Select(g => g.Key));
        var buyer = groups[0];
        Assert.Equal((2, 25m, 655_000m, 30_000m, 300_000m, 355_000m, 15m), (buyer.InvoiceCount, buyer.InvoicedKg, buyer.Revenue, buyer.PriceCuts, buyer.SalesReturns, buyer.NetRevenue, buyer.SoldKg));
        Assert.Equal((1, 60_000m), (groups[1].InvoiceCount, groups[1].Revenue));
    }

    [Fact]
    public void Grouped_by_product_the_rows_add_up_to_the_revenue_and_the_net_revenue()
    {
        var result = SalesPerformanceCalculator.Build(Facts, SalesPerformanceGrouping.Product);

        Assert.Equal(result.Totals.Revenue, result.Groups.Sum(g => g.Revenue));
        Assert.Equal(result.Totals.SalesReturns, result.Groups.Sum(g => g.SalesReturns));
        Assert.Equal(result.Totals.NetRevenue, result.Groups.Sum(g => g.NetRevenue));
        Assert.Equal(result.Totals.PriceCuts, result.Groups.Sum(g => g.PriceCuts));
        Assert.Equal(result.Totals.InvoicedKg, result.Groups.Sum(g => g.InvoicedKg));
        Assert.Equal(result.Totals.SoldKg, result.Groups.Sum(g => g.SoldKg));
        Assert.Equal([Oyster.ToString(), Other.ToString(), "discounts", "other-charges", "price-cuts"], result.Groups.Select(g => g.Key));
        var oyster = result.Groups[0];
        Assert.Equal((3, 22m, 660_000m, 300_000m, 12m), (oyster.InvoiceCount, oyster.InvoicedKg, oyster.Revenue, oyster.SalesReturns, oyster.SoldKg));
        var priceCuts = result.Groups[4];
        Assert.Equal((-30_000m, 30_000m, 0m), (priceCuts.Revenue, priceCuts.PriceCuts, priceCuts.SalesReturns));
        Assert.Equal(-20_000m, result.Groups[2].Revenue);
        Assert.Equal(5_000m, result.Groups[3].Revenue);
    }

    // The sales return takes back the whole invoice, so an invoice with a discount returned gives the discount row its part of the return.
    [Fact]
    public void A_returned_invoice_returns_its_discount_charges_and_price_cuts_too()
    {
        var invoice = One with { Id = Guid.NewGuid(), ReturnValue = 355_000m, HasReturn = true };
        var facts = new SalesPerformanceFacts([invoice], [new(invoice.Id, Oyster, "Oyster", 10m, 300_000m, 10m), new(invoice.Id, Other, "Other", 5m, 100_000m, 5m)]);

        var result = SalesPerformanceCalculator.Build(facts, SalesPerformanceGrouping.Product);

        Assert.Equal(355_000m, result.Groups.Sum(g => g.SalesReturns));
        Assert.Equal(0m, result.Groups.Sum(g => g.NetRevenue));
        Assert.Equal(-20_000m, result.Groups.Single(g => g.Key == "discounts").SalesReturns);
        Assert.Equal(0m, result.Totals.SoldKg);
    }

    [Fact]
    public void Grouped_by_zone_and_by_period_the_groups_follow_the_zone_and_the_business_date()
    {
        var zones = SalesPerformanceCalculator.Build(Facts, SalesPerformanceGrouping.Zone).Groups;
        Assert.Equal(["Zone A", "Zone B"], zones.Select(g => g.Label));
        Assert.Equal([655_000m, 60_000m], zones.Select(g => g.Revenue));

        var days = SalesPerformanceCalculator.Build(Facts, SalesPerformanceGrouping.Day).Groups;
        Assert.Equal(["2026-11-01", "2026-11-02"], days.Select(g => g.Key));

        // Sunday 1 November belongs to the week before the one that starts on Monday 2 November.
        var weeks = SalesPerformanceCalculator.Build(Facts, SalesPerformanceGrouping.Week).Groups;
        Assert.Equal(["2026-10-26", "2026-11-02"], weeks.Select(g => g.Key));
        Assert.Equal(["2026-W44", "2026-W45"], weeks.Select(g => g.Label));

        var months = SalesPerformanceCalculator.Build(Facts, SalesPerformanceGrouping.Month).Groups;
        Assert.Equal(("2026-11-01", "2026-11", 3), (months[0].Key, months[0].Label, months[0].InvoiceCount));
    }

    [Fact]
    public void The_invoices_of_a_group_are_newest_first_with_the_net_after_the_return()
    {
        var rows = SalesPerformanceCalculator.InvoicesOf(Facts, SalesPerformanceGrouping.Customer, Buyer.ToString());

        Assert.Equal(["HD000002", "HD000001"], rows.Select(r => r.Code));
        Assert.Equal((300_000m, 300_000m, 0m), (rows[0].Total, rows[0].ReturnValue, rows[0].Net));
        Assert.Equal(355_000m, rows[1].Net);
        Assert.Equal(["HD000003"], SalesPerformanceCalculator.InvoicesOf(Facts, SalesPerformanceGrouping.Customer, SalesPerformanceCalculator.WalkInKey).Select(r => r.Code));
        Assert.Null(SalesPerformanceCalculator.InvoicesOf(Facts, SalesPerformanceGrouping.Customer, SalesPerformanceCalculator.WalkInKey)[0].Customer);
    }

    [Fact]
    public void The_invoices_of_a_product_group_and_of_the_rows_of_their_own()
    {
        Assert.Equal(["HD000003", "HD000002", "HD000001"], SalesPerformanceCalculator.InvoicesOf(Facts, SalesPerformanceGrouping.Product, Oyster.ToString()).Select(r => r.Code));
        Assert.Equal(["HD000001"], SalesPerformanceCalculator.InvoicesOf(Facts, SalesPerformanceGrouping.Product, Other.ToString()).Select(r => r.Code));
        Assert.Equal(["HD000001"], SalesPerformanceCalculator.InvoicesOf(Facts, SalesPerformanceGrouping.Product, SalesPerformanceCalculator.DiscountsKey).Select(r => r.Code));
        Assert.Equal(["HD000001"], SalesPerformanceCalculator.InvoicesOf(Facts, SalesPerformanceGrouping.Product, SalesPerformanceCalculator.PriceCutsKey).Select(r => r.Code));
        Assert.Empty(SalesPerformanceCalculator.InvoicesOf(Facts, SalesPerformanceGrouping.Product, "no-such-group"));
    }

    [Fact]
    public void No_invoice_gives_no_group_and_a_zero_total()
    {
        var result = SalesPerformanceCalculator.Build(new SalesPerformanceFacts([], []), SalesPerformanceGrouping.Product);

        Assert.Empty(result.Groups);
        Assert.Equal((0, 0m, 0m), (result.Totals.InvoiceCount, result.Totals.Revenue, result.Totals.NetRevenue));
    }
}
