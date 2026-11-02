
namespace OMTS.Application.Features.WaterQuality.Dtos;

/// <summary>One chart period ("2026-10" or "2026-10-23"); <see cref="Missing"/> when it has no confirmed measurement.</summary>
public sealed record WaterChartPeriod(string Period, DateOnly From, DateOnly To, IndicatorStatistics Ph, IndicatorStatistics Salinity, bool Missing);
