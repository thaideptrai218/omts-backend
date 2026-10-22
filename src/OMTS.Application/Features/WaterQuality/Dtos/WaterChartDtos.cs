namespace OMTS.Application.Features.WaterQuality.Dtos;

public enum WaterChartGrouping
{
    Month,
    Day,
}

/// <summary>Average, minimum and maximum of one indicator in a period; empty values when <see cref="Count"/> is 0.</summary>
public sealed record IndicatorStatistics(decimal? Average, decimal? Minimum, decimal? Maximum, int Count)
{
    public static readonly IndicatorStatistics Empty = new(null, null, null, 0);
}

/// <summary>One chart period ("2026-10" or "2026-10-23"); <see cref="Missing"/> when it has no confirmed measurement.</summary>
public sealed record WaterChartPeriod(string Period, DateOnly From, DateOnly To, IndicatorStatistics Ph, IndicatorStatistics Salinity, bool Missing);

/// <summary>ENV-03 chart of a zone: confirmed measurements only, grouped by local date (Asia/Ho_Chi_Minh), with the current thresholds for the bands.</summary>
public sealed record WaterChart(
    Guid ZoneId,
    DateOnly From,
    DateOnly To,
    WaterChartGrouping GroupBy,
    WaterThresholds Thresholds,
    IReadOnlyList<WaterChartPeriod> Periods);
