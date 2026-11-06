namespace OMTS.Application.Features.WaterQuality.Dtos;

/// <summary>One active measurement on the chart. Aggregates by day or month stay on the client.</summary>
public sealed record WaterChartPoint(Guid Id, DateTimeOffset MeasuredAt, decimal Ph, decimal Salinity, decimal Temperature);

/// <summary>WATER-03 chart of a zone: its active measurements in the period, oldest first, with the current thresholds for the bands.</summary>
public sealed record WaterChart(
    Guid ZoneId,
    DateOnly From,
    DateOnly To,
    WaterThresholds Thresholds,
    IReadOnlyList<WaterChartPoint> Points);
