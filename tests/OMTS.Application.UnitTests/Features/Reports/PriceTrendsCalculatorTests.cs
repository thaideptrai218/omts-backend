using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Reports;
using OMTS.Application.Features.Reports.PriceTrends;

namespace OMTS.Application.UnitTests.Features.Reports;

/// <summary>BR-REPORT-02 as a pure function: the average weighted by kg, the lowest and highest unit price, the buckets and the lines of a period.</summary>
public class PriceTrendsCalculatorTests
{
    private static readonly Guid Oyster = Guid.NewGuid();
    private static readonly Guid Clam = Guid.NewGuid();
    private static readonly Guid Buyer = Guid.NewGuid();

    private static PriceTrendLine Line(string code, DateOnly date, Guid product, string name, decimal kg, decimal unitPrice, Guid? customer = null) =>
        new(Guid.NewGuid(), code, date, customer, customer is null ? null : "Buyer", product, name, kg, unitPrice, Math.Round(kg * unitPrice, MidpointRounding.AwayFromZero));

    // 2026-11-02 is a Monday (ISO week 45); 2026-11-08 the Sunday of that week; 2026-11-09 the next Monday.
    private static readonly PriceTrendLine[] Lines =
    [
        Line("HD000001", new DateOnly(2026, 11, 2), Oyster, "Oyster", 10m, 30_000m, Buyer),
        Line("HD000002", new DateOnly(2026, 11, 2), Oyster, "Oyster", 30m, 20_000m),
        Line("HD000003", new DateOnly(2026, 11, 8), Oyster, "Oyster", 5m, 40_000m, Buyer),
        Line("HD000004", new DateOnly(2026, 11, 9), Oyster, "Oyster", 2m, 25_000m),
        Line("HD000005", new DateOnly(2026, 11, 2), Clam, "Clam", 4m, 12_500.5m),
    ];

    // What the database does: the lines added up per product and invoice date.
    private static IReadOnlyList<PriceTrendDay> Days(IEnumerable<PriceTrendLine> lines) =>
        lines.GroupBy(l => (l.ProductId, l.ProductName, l.Date))
            .Select(g => new PriceTrendDay(g.Key.ProductId, g.Key.ProductName, g.Key.Date, g.Sum(l => l.Kg), g.Sum(l => l.Amount), g.Min(l => l.UnitPrice), g.Max(l => l.UnitPrice)))
            .ToList();

    [Fact]
    public void The_average_is_weighted_by_kg_and_differs_from_the_simple_average_of_the_unit_prices()
    {
        var day = PriceTrendsCalculator.Build(Days(Lines), ReportPeriodUnit.Day).Single(s => s.ProductId == Oyster).Periods[0];

        // (300,000 + 600,000) / 40 kg = 22,500; the simple average of 30,000 and 20,000 would be 25,000.
        Assert.Equal(("2026-11-02", 40m, 22_500m, 20_000m, 30_000m), (day.Key, day.SoldKg, day.AveragePrice, day.LowestPrice, day.HighestPrice));
    }

    [Fact]
    public void Prices_are_not_rounded()
    {
        var clam = PriceTrendsCalculator.Build(Days(Lines), ReportPeriodUnit.Day).Single(s => s.ProductId == Clam).Periods.Single();

        // 4 kg at 12,500.5 is 50,002 to the dong; the average of the amounts is 12,500.5, not a whole number.
        Assert.Equal((12_500.5m, 12_500.5m, 12_500.5m), (clam.AveragePrice, clam.LowestPrice, clam.HighestPrice));
        // 1 kg at 10 and 2 kg at 11 are 32 VND for 3 kg: 10.666..., kept as it is.
        var thirds = new[] { Line("HD000009", new DateOnly(2026, 11, 2), Clam, "Clam", 1m, 10m), Line("HD000010", new DateOnly(2026, 11, 2), Clam, "Clam", 2m, 11m) };
        Assert.Equal(32m / 3m, PriceTrendsCalculator.Build(Days(thirds), ReportPeriodUnit.Day)[0].Periods[0].AveragePrice);
    }

    [Fact]
    public void Weeks_start_on_monday_and_months_on_the_first_and_periods_come_in_time_order()
    {
        var weeks = PriceTrendsCalculator.Build(Days(Lines), ReportPeriodUnit.Week).Single(s => s.ProductId == Oyster).Periods;
        var months = PriceTrendsCalculator.Build(Days(Lines), ReportPeriodUnit.Month).Single(s => s.ProductId == Oyster).Periods;
        var days = PriceTrendsCalculator.Build(Days(Lines), ReportPeriodUnit.Day).Single(s => s.ProductId == Oyster).Periods;

        // The Sunday belongs to the week of the Monday before; the next Monday starts week 46.
        Assert.Equal(["2026-11-02", "2026-11-09"], weeks.Select(p => p.Key));
        Assert.Equal(["2026-W45", "2026-W46"], weeks.Select(p => p.Label));
        Assert.Equal([45m, 2m], weeks.Select(p => p.SoldKg));
        Assert.Equal(("2026-11-01", "2026-11", 47m), (months[0].Key, months[0].Label, months[0].SoldKg));
        Assert.Equal(["2026-11-02", "2026-11-08", "2026-11-09"], days.Select(p => p.Key));
    }

    [Fact]
    public void Each_product_has_its_own_series_in_name_order()
    {
        var series = PriceTrendsCalculator.Build(Days(Lines), ReportPeriodUnit.Day);

        Assert.Equal(["Clam", "Oyster"], series.Select(s => s.ProductName));
    }

    [Fact]
    public void Any_day_of_a_period_names_its_first_and_last_day_for_the_database_to_read_only_its_lines()
    {
        Assert.Equal((new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 8)), PriceTrendsCalculator.BucketOf(new DateOnly(2026, 11, 5), ReportPeriodUnit.Week));
        Assert.Equal((new DateOnly(2026, 11, 8), new DateOnly(2026, 11, 8)), PriceTrendsCalculator.BucketOf(new DateOnly(2026, 11, 8), ReportPeriodUnit.Day));
        Assert.Equal((new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30)), PriceTrendsCalculator.BucketOf(new DateOnly(2026, 11, 17), ReportPeriodUnit.Month));
        // A leap February, and the week that starts on Monday 28 December and ends in the new year.
        Assert.Equal((new DateOnly(2028, 2, 1), new DateOnly(2028, 2, 29)), PriceTrendsCalculator.BucketOf(new DateOnly(2028, 2, 10), ReportPeriodUnit.Month));
        Assert.Equal((new DateOnly(2026, 12, 28), new DateOnly(2027, 1, 3)), PriceTrendsCalculator.BucketOf(new DateOnly(2027, 1, 1), ReportPeriodUnit.Week));
        // The last week of the calendar ends with the calendar instead of past it.
        Assert.Equal(DateOnly.MaxValue, PriceTrendsCalculator.BucketOf(DateOnly.MaxValue, ReportPeriodUnit.Week).To);
    }

    [Fact]
    public void The_lines_of_a_period_are_listed_newest_first_with_their_customer()
    {
        var week = Lines.Where(l => l.ProductId == Oyster && l.Date <= new DateOnly(2026, 11, 8));

        var rows = PriceTrendsCalculator.LineRowsOf(week);

        Assert.Equal(["HD000003", "HD000002", "HD000001"], rows.Select(l => l.InvoiceCode));
        Assert.Null(rows.Single(l => l.InvoiceCode == "HD000002").Customer);
        Assert.Equal(Buyer, rows.Single(l => l.InvoiceCode == "HD000001").Customer!.Id);
        Assert.Empty(PriceTrendsCalculator.LineRowsOf([]));
    }

    [Fact]
    public void A_week_or_a_month_is_the_sum_of_its_days_with_the_lowest_and_the_highest_price_of_them()
    {
        var oyster = Days(Lines.Where(l => l.ProductId == Oyster));

        var week = PriceTrendsCalculator.Build(oyster, ReportPeriodUnit.Week).Single().Periods[0];

        // Two days of the week: 40 kg for 900,000 and 5 kg for 200,000: (900,000 + 200,000) / 45 kg.
        Assert.Equal((45m, 1_100_000m / 45m, 20_000m, 40_000m), (week.SoldKg, week.AveragePrice, week.LowestPrice, week.HighestPrice));
    }

    [Fact]
    public void No_line_gives_no_series()
    {
        Assert.Empty(PriceTrendsCalculator.Build([], ReportPeriodUnit.Day));
    }
}
