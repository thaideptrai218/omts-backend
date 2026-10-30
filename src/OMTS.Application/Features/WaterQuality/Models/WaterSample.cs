using System.Globalization;
using OMTS.Application.Common;
using OMTS.Application.Features.WaterQuality.Dtos;

namespace OMTS.Application.Features.WaterQuality.Models;

/// <summary>A confirmed measurement as the chart and the monthly summary see it.</summary>
public sealed record WaterSample(DateTimeOffset MeasuredAt, decimal Ph, decimal Salinity);
