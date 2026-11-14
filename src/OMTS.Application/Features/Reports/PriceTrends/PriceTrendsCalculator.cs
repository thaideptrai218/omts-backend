using OMTS.Application.Common.Interfaces.Persistence;

namespace OMTS.Application.Features.Reports.PriceTrends;

/// <summary>
/// BR-REPORT-02 as pure functions, so the figures are unit tested and one rule serves the report, its lines and its file. The sold kg of a period is the kg of the lines counted (an invoice
/// with a sales return is not counted at all, so no returned kg is left to subtract); the average unit price is the total of the line amounts divided by the total kg; the lowest and highest
/// are unit prices of the lines. Nothing is rounded, and discounts, other charges and price cuts are not spread over the lines.
/// </summary>
public static class PriceTrendsCalculator
{
    public static IReadOnlyList<PriceTrendSeries> Build(IReadOnlyList<PriceTrendLine> lines, ReportPeriodUnit unit) =>
        lines.GroupBy(l => l.ProductId)
            .Select(product => new PriceTrendSeries(
                product.Key,
                product.First().ProductName,
                product.GroupBy(l => ReportPeriodBuckets.StartOf(l.Date, unit)).OrderBy(g => g.Key).Select(g => Period(g.Key, unit, g.ToList())).ToList()))
            .OrderBy(s => s.ProductName, StringComparer.Ordinal)
            .ThenBy(s => s.ProductId)
            .ToList();

    /// <summary>The lines of the product in the day, week or month that holds <paramref name="periodDate"/> (any day of it), newest first (REPORT-02.2).</summary>
    public static IReadOnlyList<PriceTrendLineRow> LinesOf(IReadOnlyList<PriceTrendLine> lines, ReportPeriodUnit unit, Guid productId, DateOnly periodDate)
    {
        var start = ReportPeriodBuckets.StartOf(periodDate, unit);
        return lines
            .Where(l => l.ProductId == productId && ReportPeriodBuckets.StartOf(l.Date, unit) == start)
            .OrderByDescending(l => l.Date).ThenByDescending(l => l.InvoiceCode, StringComparer.Ordinal)
            .Select(l => new PriceTrendLineRow(
                l.InvoiceId, l.InvoiceCode, l.Date, l.CustomerId is { } id ? new PriceTrendCustomer(id, l.CustomerName ?? string.Empty) : null, l.Kg, l.UnitPrice, l.Amount))
            .ToList();
    }

    private static PriceTrendPeriod Period(DateOnly start, ReportPeriodUnit unit, List<PriceTrendLine> lines)
    {
        var kg = lines.Sum(l => l.Kg);
        return new PriceTrendPeriod(
            ReportPeriodBuckets.Key(start), ReportPeriodBuckets.Label(start, unit), kg, kg == 0m ? 0m : lines.Sum(l => l.Amount) / kg, lines.Min(l => l.UnitPrice), lines.Max(l => l.UnitPrice));
    }
}
