using OMTS.Application.Features.WaterQuality.Models;
using System.Globalization;
using OMTS.Application.Common;
using OMTS.Application.Features.WaterQuality.Dtos;

namespace OMTS.Application.Features.WaterQuality;

/// <summary>
/// ENV-03 chart aggregation: one period per month or day of the range (by local date in
/// Asia/Ho_Chi_Minh), each with average/minimum/maximum/count per indicator; a period without a
/// measurement is returned with <c>Missing = true</c>.
/// </summary>
public static class WaterChartBuilder
{
    /// <summary>Longest range that can be grouped by day.</summary>
    public const int MaxDays = 366;

    public static IReadOnlyList<WaterChartPeriod> Build(IEnumerable<WaterSample> samples, WaterPeriod range, WaterChartGrouping groupBy)
    {
        var byPeriod = samples
            .Select(s => (Date: BusinessClock.ToBusinessDate(s.MeasuredAt), Sample: s))
            .Where(x => x.Date >= range.From && x.Date <= range.To)
            .GroupBy(x => KeyOf(x.Date, groupBy))
            .ToDictionary(g => g.Key, g => g.Select(x => x.Sample).ToList());

        var periods = new List<WaterChartPeriod>();
        var cursor = groupBy == WaterChartGrouping.Month ? new DateOnly(range.From.Year, range.From.Month, 1) : range.From;
        while (cursor <= range.To)
        {
            var next = groupBy == WaterChartGrouping.Month ? cursor.AddMonths(1) : cursor.AddDays(1);
            var key = KeyOf(cursor, groupBy);
            var items = byPeriod.GetValueOrDefault(key) ?? [];
            periods.Add(new WaterChartPeriod(
                key,
                cursor < range.From ? range.From : cursor,
                next.AddDays(-1) > range.To ? range.To : next.AddDays(-1),
                Statistics(items.Select(i => i.Ph)),
                Statistics(items.Select(i => i.Salinity)),
                items.Count == 0));
            cursor = next;
        }

        return periods;
    }

    private static string KeyOf(DateOnly date, WaterChartGrouping groupBy) =>
        date.ToString(groupBy == WaterChartGrouping.Month ? "yyyy-MM" : "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static IndicatorStatistics Statistics(IEnumerable<decimal> values)
    {
        var list = values.ToList();
        return list.Count == 0
            ? IndicatorStatistics.Empty
            : new IndicatorStatistics(Math.Round(list.Average(), 2, MidpointRounding.AwayFromZero), list.Min(), list.Max(), list.Count);
    }
}
