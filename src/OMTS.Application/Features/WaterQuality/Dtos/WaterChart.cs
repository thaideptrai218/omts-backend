
namespace OMTS.Application.Features.WaterQuality.Dtos;

/// <summary>ENV-03 chart of a zone: confirmed measurements only, grouped by local date (Asia/Ho_Chi_Minh), with the current thresholds for the bands.</summary>
public sealed record WaterChart(
    Guid ZoneId,
    DateOnly From,
    DateOnly To,
    WaterChartGrouping GroupBy,
    WaterThresholds Thresholds,
    IReadOnlyList<WaterChartPeriod> Periods);
