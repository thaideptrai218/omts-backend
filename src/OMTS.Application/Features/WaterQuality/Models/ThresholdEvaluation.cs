using System.Globalization;
using OMTS.Application.Features.WaterQuality.Dtos;

namespace OMTS.Application.Features.WaterQuality.Models;

/// <summary>Result of comparing confirmed values with a threshold set.</summary>
/// <param name="ViolatedMetrics">Indicator names for MSG27, e.g. ["pH", "Salinity"].</param>
/// <param name="ViolatedValues">Stored text, e.g. "pH 9.10 &gt; 8.50; Salinity 12.00 &lt; 15.00".</param>
public sealed record ThresholdEvaluation(bool Exceeded, IReadOnlyList<string> ViolatedMetrics, string? ViolatedValues);
