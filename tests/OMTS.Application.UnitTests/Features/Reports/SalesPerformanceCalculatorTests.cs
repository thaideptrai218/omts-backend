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

    // One invoice line with the kg a sales return that stands took back, and the invoices with their lines: what the database reads, before it adds them up.
    private sealed record TestLine(Guid InvoiceId, Guid ProductId, string ProductName, decimal Kg, decimal Amount, decimal ReturnedKg);

    private sealed record TestFacts(IReadOnlyList<SalesPerformanceInvoice> Invoices, IReadOnlyList<TestLine> Lines);

    private static SalesPerformanceCalculation Build(TestFacts facts, SalesPerformanceGrouping grouping) =>
        SalesPerformanceCalculator.Build(Aggregate(facts, grouping), grouping);

    // The sums the database makes per slice, written plainly: an invoice is in one slice (its customer, zone or day); by product a slice is the lines of the product, and the discounts, the
    // other charges and the price cuts of the invoices are parts of their own, each with the part that was returned with the invoice.
    private static SalesPerformanceAggregates Aggregate(TestFacts facts, SalesPerformanceGrouping grouping)
    {
        SalesPerformanceSums OfInvoices(IEnumerable<SalesPerformanceInvoice> invoices)
        {
            var list = invoices.ToList();
            var lines = list.SelectMany(i => facts.Lines.Where(l => l.InvoiceId == i.Id)).ToList();
            return new SalesPerformanceSums(
                list.Count, lines.Sum(l => l.Kg), list.Sum(i => i.Total), list.Sum(i => i.PriceCuts), list.Sum(i => i.ReturnValue), lines.Sum(l => l.Kg - l.ReturnedKg));
        }

        var total = OfInvoices(facts.Invoices);
        switch (grouping)
        {
            case SalesPerformanceGrouping.Customer:
                return new(total, facts.Invoices.GroupBy(i => i.CustomerId).Select(g => new SalesPerformanceSlice(g.Key, g.First().CustomerName, null, OfInvoices(g))).ToList(), SalesPerformanceParts.None);
            case SalesPerformanceGrouping.Zone:
                return new(total, facts.Invoices.GroupBy(i => i.ZoneId).Select(g => new SalesPerformanceSlice(g.Key, g.First().ZoneName, null, OfInvoices(g))).ToList(), SalesPerformanceParts.None);
            case SalesPerformanceGrouping.Product:
                var slices = facts.Lines.GroupBy(l => l.ProductId).Select(g =>
                {
                    var returned = g.Where(l => facts.Invoices.Single(i => i.Id == l.InvoiceId).HasReturn).Sum(l => l.Amount);
                    return new SalesPerformanceSlice(
                        g.Key, g.First().ProductName, null,
                        new SalesPerformanceSums(g.Select(l => l.InvoiceId).Distinct().Count(), g.Sum(l => l.Kg), g.Sum(l => l.Amount), 0m, returned, g.Sum(l => l.Kg - l.ReturnedKg)));
                }).ToList();
                SalesPerformanceSums Part(Func<SalesPerformanceInvoice, decimal> amount, int sign, bool priceCuts) => new(
                    facts.Invoices.Count(i => amount(i) != 0m), 0m, sign * facts.Invoices.Sum(amount), priceCuts ? facts.Invoices.Sum(amount) : 0m,
                    sign * facts.Invoices.Where(i => i.HasReturn).Sum(amount), 0m);
                return new(total, slices, new SalesPerformanceParts(Part(i => i.Discount, -1, false), Part(i => i.OtherCharges, 1, false), Part(i => i.PriceCuts, -1, true)));
            default:
                return new(total, facts.Invoices.GroupBy(i => i.Date).Select(g => new SalesPerformanceSlice(null, null, g.Key, OfInvoices(g))).ToList(), SalesPerformanceParts.None);
        }
    }

    // The invoices the database reads for a group, written plainly, then shown as the report does.
    private static IReadOnlyList<SalesPerformanceInvoiceRow> InvoicesOf(TestFacts facts, SalesPerformanceGrouping grouping, string key)
    {
        if (SalesPerformanceCalculator.GroupOf(grouping, key) is not { } group)
        {
            return [];
        }

        bool In(SalesPerformanceInvoice i) => group.Kind switch
        {
            SalesPerformanceGroupKind.WalkIn => i.CustomerId is null,
            SalesPerformanceGroupKind.Customer => i.CustomerId == group.Id,
            SalesPerformanceGroupKind.Zone => i.ZoneId == group.Id,
            SalesPerformanceGroupKind.DateRange => i.Date >= group.From && i.Date <= group.To,
            SalesPerformanceGroupKind.Product => facts.Lines.Any(l => l.InvoiceId == i.Id && l.ProductId == group.Id),
            SalesPerformanceGroupKind.Discounts => i.Discount != 0m,
            SalesPerformanceGroupKind.OtherCharges => i.OtherCharges != 0m,
            _ => i.PriceCuts != 0m,
        };
        return SalesPerformanceCalculator.InvoiceRowsOf(facts.Invoices.Where(In));
    }

    private static readonly TestFacts Facts = new(
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
        var totals = Build(Facts, SalesPerformanceGrouping.Customer).Totals;

        // Revenue is the invoice totals; net revenue is revenue minus the sales returns; sold kg is invoiced kg minus returned kg.
        Assert.Equal((3, 27m, 715_000m, 30_000m, 300_000m, 415_000m, 17m), (totals.InvoiceCount, totals.InvoicedKg, totals.Revenue, totals.PriceCuts, totals.SalesReturns, totals.NetRevenue, totals.SoldKg));
        Assert.Null(totals.NetMoneyReceived);
    }

    [Fact]
    public void Grouped_by_customer_the_walk_in_invoices_have_a_row_of_their_own()
    {
        var groups = Build(Facts, SalesPerformanceGrouping.Customer).Groups;

        Assert.Equal([Buyer.ToString(), SalesPerformanceCalculator.WalkInKey], groups.Select(g => g.Key));
        var buyer = groups[0];
        Assert.Equal((2, 25m, 655_000m, 30_000m, 300_000m, 355_000m, 15m), (buyer.InvoiceCount, buyer.InvoicedKg, buyer.Revenue, buyer.PriceCuts, buyer.SalesReturns, buyer.NetRevenue, buyer.SoldKg));
        Assert.Equal((1, 60_000m), (groups[1].InvoiceCount, groups[1].Revenue));
    }

    [Fact]
    public void Grouped_by_product_the_rows_add_up_to_the_revenue_and_the_net_revenue()
    {
        var result = Build(Facts, SalesPerformanceGrouping.Product);

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
        var facts = new TestFacts([invoice], [new(invoice.Id, Oyster, "Oyster", 10m, 300_000m, 10m), new(invoice.Id, Other, "Other", 5m, 100_000m, 5m)]);

        var result = Build(facts, SalesPerformanceGrouping.Product);

        Assert.Equal(355_000m, result.Groups.Sum(g => g.SalesReturns));
        Assert.Equal(0m, result.Groups.Sum(g => g.NetRevenue));
        Assert.Equal(-20_000m, result.Groups.Single(g => g.Key == "discounts").SalesReturns);
        Assert.Equal(0m, result.Totals.SoldKg);
    }

    [Fact]
    public void Grouped_by_zone_and_by_period_the_groups_follow_the_zone_and_the_business_date()
    {
        var zones = Build(Facts, SalesPerformanceGrouping.Zone).Groups;
        Assert.Equal(["Zone A", "Zone B"], zones.Select(g => g.Label));
        Assert.Equal([655_000m, 60_000m], zones.Select(g => g.Revenue));

        var days = Build(Facts, SalesPerformanceGrouping.Day).Groups;
        Assert.Equal(["2026-11-01", "2026-11-02"], days.Select(g => g.Key));

        // Sunday 1 November belongs to the week before the one that starts on Monday 2 November.
        var weeks = Build(Facts, SalesPerformanceGrouping.Week).Groups;
        Assert.Equal(["2026-10-26", "2026-11-02"], weeks.Select(g => g.Key));
        Assert.Equal(["2026-W44", "2026-W45"], weeks.Select(g => g.Label));

        var months = Build(Facts, SalesPerformanceGrouping.Month).Groups;
        Assert.Equal(("2026-11-01", "2026-11", 3), (months[0].Key, months[0].Label, months[0].InvoiceCount));
    }

    [Fact]
    public void The_invoices_of_a_group_are_newest_first_with_the_net_after_the_return()
    {
        var rows = InvoicesOf(Facts, SalesPerformanceGrouping.Customer, Buyer.ToString());

        Assert.Equal(["HD000002", "HD000001"], rows.Select(r => r.Code));
        Assert.Equal((300_000m, 300_000m, 0m), (rows[0].Total, rows[0].ReturnValue, rows[0].Net));
        Assert.Equal(355_000m, rows[1].Net);
        Assert.Equal(["HD000003"], InvoicesOf(Facts, SalesPerformanceGrouping.Customer, SalesPerformanceCalculator.WalkInKey).Select(r => r.Code));
        Assert.Null(InvoicesOf(Facts, SalesPerformanceGrouping.Customer, SalesPerformanceCalculator.WalkInKey)[0].Customer);
    }

    [Fact]
    public void The_invoices_of_a_product_group_and_of_the_rows_of_their_own()
    {
        Assert.Equal(["HD000003", "HD000002", "HD000001"], InvoicesOf(Facts, SalesPerformanceGrouping.Product, Oyster.ToString()).Select(r => r.Code));
        Assert.Equal(["HD000001"], InvoicesOf(Facts, SalesPerformanceGrouping.Product, Other.ToString()).Select(r => r.Code));
        Assert.Equal(["HD000001"], InvoicesOf(Facts, SalesPerformanceGrouping.Product, SalesPerformanceCalculator.DiscountsKey).Select(r => r.Code));
        Assert.Equal(["HD000001"], InvoicesOf(Facts, SalesPerformanceGrouping.Product, SalesPerformanceCalculator.PriceCutsKey).Select(r => r.Code));
        Assert.Empty(InvoicesOf(Facts, SalesPerformanceGrouping.Product, "no-such-group"));
    }

    [Fact]
    public void The_key_of_a_group_names_what_its_invoices_have_in_common()
    {
        Assert.Equal(new SalesPerformanceGroupFilter(SalesPerformanceGroupKind.WalkIn), SalesPerformanceCalculator.GroupOf(SalesPerformanceGrouping.Customer, "Walk-In"));
        Assert.Equal(new SalesPerformanceGroupFilter(SalesPerformanceGroupKind.Customer, Buyer), SalesPerformanceCalculator.GroupOf(SalesPerformanceGrouping.Customer, Buyer.ToString().ToUpperInvariant()));
        Assert.Equal(new SalesPerformanceGroupFilter(SalesPerformanceGroupKind.Zone, ZoneA), SalesPerformanceCalculator.GroupOf(SalesPerformanceGrouping.Zone, ZoneA.ToString()));
        Assert.Equal(new SalesPerformanceGroupFilter(SalesPerformanceGroupKind.Product, Oyster), SalesPerformanceCalculator.GroupOf(SalesPerformanceGrouping.Product, Oyster.ToString()));
        Assert.Equal(SalesPerformanceGroupKind.OtherCharges, SalesPerformanceCalculator.GroupOf(SalesPerformanceGrouping.Product, SalesPerformanceCalculator.OtherChargesKey)!.Kind);

        // A key of another grouping, a name in the wrong case where the report writes it exactly, or one that is no id names no group.
        Assert.Null(SalesPerformanceCalculator.GroupOf(SalesPerformanceGrouping.Zone, SalesPerformanceCalculator.WalkInKey));
        Assert.Null(SalesPerformanceCalculator.GroupOf(SalesPerformanceGrouping.Customer, SalesPerformanceService.UnassignedKey));
        Assert.Null(SalesPerformanceCalculator.GroupOf(SalesPerformanceGrouping.Product, "Discounts"));
        Assert.Null(SalesPerformanceCalculator.GroupOf(SalesPerformanceGrouping.Customer, "{" + Buyer + "}"));
    }

    [Fact]
    public void The_key_of_a_period_names_its_days_and_only_the_first_day_of_a_period_is_a_key()
    {
        Assert.Equal(new SalesPerformanceGroupFilter(SalesPerformanceGroupKind.DateRange, null, new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 2)), SalesPerformanceCalculator.GroupOf(SalesPerformanceGrouping.Day, "2026-11-02"));
        Assert.Equal(new SalesPerformanceGroupFilter(SalesPerformanceGroupKind.DateRange, null, new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 8)), SalesPerformanceCalculator.GroupOf(SalesPerformanceGrouping.Week, "2026-11-02"));
        Assert.Equal(new SalesPerformanceGroupFilter(SalesPerformanceGroupKind.DateRange, null, new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28)), SalesPerformanceCalculator.GroupOf(SalesPerformanceGrouping.Month, "2026-02-01"));

        // A Tuesday is no week key and the 2nd no month key (the report sends the first day of a week or month), nor is a date written another way.
        Assert.Null(SalesPerformanceCalculator.GroupOf(SalesPerformanceGrouping.Week, "2026-11-03"));
        Assert.Null(SalesPerformanceCalculator.GroupOf(SalesPerformanceGrouping.Month, "2026-02-02"));
        Assert.Null(SalesPerformanceCalculator.GroupOf(SalesPerformanceGrouping.Day, "2026-1-2"));
        Assert.Null(SalesPerformanceCalculator.GroupOf(SalesPerformanceGrouping.Day, "2026-W45"));
    }

    [Fact]
    public void The_invoices_of_a_period_group_are_those_dated_in_it()
    {
        Assert.Equal(["HD000003", "HD000002"], InvoicesOf(Facts, SalesPerformanceGrouping.Day, "2026-11-02").Select(r => r.Code));
        Assert.Equal(["HD000001"], InvoicesOf(Facts, SalesPerformanceGrouping.Week, "2026-10-26").Select(r => r.Code));
        Assert.Equal(["HD000003", "HD000002", "HD000001"], InvoicesOf(Facts, SalesPerformanceGrouping.Month, "2026-11-01").Select(r => r.Code));
    }

    [Fact]
    public void No_invoice_gives_no_group_and_a_zero_total()
    {
        var result = Build(new TestFacts([], []), SalesPerformanceGrouping.Product);

        Assert.Empty(result.Groups);
        Assert.Equal((0, 0m, 0m), (result.Totals.InvoiceCount, result.Totals.Revenue, result.Totals.NetRevenue));
    }
}
