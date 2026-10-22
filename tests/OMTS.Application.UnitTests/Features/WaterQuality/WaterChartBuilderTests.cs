using OMTS.Application.Features.WaterQuality;
using OMTS.Application.Features.WaterQuality.Dtos;

namespace OMTS.Application.UnitTests.Features.WaterQuality;

/// <summary>ENV-03 chart aggregation by local date (UTC+7), with missing periods flagged.</summary>
public class WaterChartBuilderTests
{
    private static readonly TimeSpan Local = TimeSpan.FromHours(7);

    [Fact]
    public void Months_without_measurements_are_flagged_missing_and_statistics_are_per_indicator()
    {
        var samples = new[]
        {
            new WaterSample(new DateTimeOffset(2026, 8, 3, 8, 0, 0, Local), 7.0m, 20m),
            new WaterSample(new DateTimeOffset(2026, 8, 20, 8, 0, 0, Local), 8.0m, 25m),
            new WaterSample(new DateTimeOffset(2026, 10, 1, 8, 0, 0, Local), 7.5m, 30m),
        };

        var periods = WaterChartBuilder.Build(samples, new WaterPeriod(new DateOnly(2026, 8, 1), new DateOnly(2026, 10, 23)), WaterChartGrouping.Month);

        Assert.Equal(["2026-08", "2026-09", "2026-10"], periods.Select(p => p.Period));
        Assert.Equal(new IndicatorStatistics(7.5m, 7.0m, 8.0m, 2), periods[0].Ph);
        Assert.Equal(new IndicatorStatistics(22.5m, 20m, 25m, 2), periods[0].Salinity);
        Assert.True(periods[1].Missing);
        Assert.Equal(IndicatorStatistics.Empty, periods[1].Ph);
        Assert.False(periods[2].Missing);
        Assert.Equal((new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 23)), (periods[2].From, periods[2].To));
    }

    [Fact]
    public void Partial_first_month_starts_at_the_range_start()
    {
        var periods = WaterChartBuilder.Build([], new WaterPeriod(new DateOnly(2026, 9, 15), new DateOnly(2026, 10, 10)), WaterChartGrouping.Month);

        Assert.Equal(2, periods.Count);
        Assert.Equal(new DateOnly(2026, 9, 15), periods[0].From);
        Assert.Equal(new DateOnly(2026, 9, 30), periods[0].To);
        Assert.All(periods, p => Assert.True(p.Missing));
    }

    [Fact]
    public void Grouping_uses_the_local_date_not_the_utc_date()
    {
        // 31/10 18:30 UTC is 01/11 01:30 in Asia/Ho_Chi_Minh.
        var samples = new[] { new WaterSample(new DateTimeOffset(2026, 10, 31, 18, 30, 0, TimeSpan.Zero), 7m, 20m) };

        var periods = WaterChartBuilder.Build(samples, new WaterPeriod(new DateOnly(2026, 10, 31), new DateOnly(2026, 11, 1)), WaterChartGrouping.Day);

        Assert.Equal(["2026-10-31", "2026-11-01"], periods.Select(p => p.Period));
        Assert.True(periods[0].Missing);
        Assert.Equal(1, periods[1].Ph.Count);
    }

    [Fact]
    public void Average_is_rounded_to_two_decimals_and_samples_outside_the_range_are_ignored()
    {
        var samples = new[]
        {
            new WaterSample(new DateTimeOffset(2026, 10, 5, 8, 0, 0, Local), 7.01m, 10m),
            new WaterSample(new DateTimeOffset(2026, 10, 5, 9, 0, 0, Local), 7.02m, 10m),
            new WaterSample(new DateTimeOffset(2026, 10, 5, 10, 0, 0, Local), 7.02m, 10m),
            new WaterSample(new DateTimeOffset(2026, 10, 6, 8, 0, 0, Local), 14m, 100m),
        };

        var day = Assert.Single(WaterChartBuilder.Build(samples, new WaterPeriod(new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 5)), WaterChartGrouping.Day));

        Assert.Equal(7.02m, day.Ph.Average);
        Assert.Equal(3, day.Ph.Count);
        Assert.Equal(7.02m, day.Ph.Maximum);
    }
}
