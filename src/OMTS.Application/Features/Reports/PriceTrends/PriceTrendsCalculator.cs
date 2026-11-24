using OMTS.Application.Common.Interfaces.Persistence;

namespace OMTS.Application.Features.Reports.PriceTrends;

/// <summary>
/// BR-REPORT-02 as pure functions, so the figures are unit tested and one rule serves the report, its lines and its file. The sold kg of a period is the kg of the lines counted (an invoice
/// with a sales return is not counted at all, so no returned kg is left to subtract); the average unit price is the total of the line amounts divided by the total kg; the lowest and highest
/// are unit prices of the lines. Nothing is rounded, and discounts, other charges and price cuts are not spread over the lines. The database adds the lines up per product and day
/// (<see cref="PriceTrendDay"/>); a week or a month is the sum of its days, with the lowest and the highest of their prices.
/// </summary>
public static class PriceTrendsCalculator
{
    public static IReadOnlyList<PriceTrendSeries> Build(IReadOnlyList<PriceTrendDay> days, ReportPeriodUnit unit) =>
        days.GroupBy(d => d.ProductId)
            .Select(product => new PriceTrendSeries(
                product.Key,
                product.First().ProductName,
                product.GroupBy(d => ReportPeriodBuckets.StartOf(d.Date, unit)).OrderBy(g => g.Key).Select(g => Period(g.Key, unit, g.ToList())).ToList()))
            .OrderBy(s => s.ProductName, StringComparer.Ordinal)
            .ThenBy(s => s.ProductId)
            .ToList();

    /// <summary>The first and the last day of the day, week or month that holds <paramref name="periodDate"/> (any day of it), for the database to read only its lines.</summary>
    public static (DateOnly From, DateOnly To) BucketOf(DateOnly periodDate, ReportPeriodUnit unit)
    {
        var start = ReportPeriodBuckets.StartOf(periodDate, unit);
        return (start, ReportPeriodBuckets.EndOf(start, unit));
    }

    /// <summary>The lines of one product and period as report rows, newest first (REPORT-02.2).</summary>
    public static IReadOnlyList<PriceTrendLineRow> LineRowsOf(IEnumerable<PriceTrendLine> lines) =>
        lines
            .OrderByDescending(l => l.Date).ThenByDescending(l => l.InvoiceCode, StringComparer.Ordinal)
            .Select(l => new PriceTrendLineRow(
                l.InvoiceId, l.InvoiceCode, l.Date, l.CustomerId is { } id ? new PriceTrendCustomer(id, l.CustomerName ?? string.Empty) : null, l.Kg, l.UnitPrice, l.Amount))
            .ToList();

    private static PriceTrendPeriod Period(DateOnly start, ReportPeriodUnit unit, List<PriceTrendDay> days)
    {
        var kg = days.Sum(d => d.Kg);
        return new PriceTrendPeriod(
            ReportPeriodBuckets.Key(start), ReportPeriodBuckets.Label(start, unit), kg, kg == 0m ? 0m : days.Sum(d => d.Amount) / kg, days.Min(d => d.LowestPrice), days.Max(d => d.HighestPrice));
    }
}
