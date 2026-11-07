using System.Globalization;
using OMTS.Application.Common.Messages;
using OMTS.Application.Features.WaterQuality.Dtos;

namespace OMTS.Application.Features.WaterQuality;

/// <summary>Result of comparing values with a threshold set.</summary>
/// <param name="ViolatedMetrics">Indicator names, e.g. ["pH", "Salinity"].</param>
/// <param name="ViolatedValues">Stored text, e.g. "pH 9.10 &gt; 8.50; Salinity 12.00 &lt; 15.00".</param>
/// <param name="ValuesOutside">The values outside with their allowed range, for MSG-WATER-02/03; empty when nothing is outside.</param>
public sealed record ThresholdEvaluation(
    bool Exceeded, IReadOnlyList<string> ViolatedMetrics, string? ViolatedValues, IReadOnlyList<WaterValueOutside> ValuesOutside);

/// <summary>
/// BR-WATER-04: a value below its minimum or above its maximum is outside; a value equal to a bound is within it, and
/// a bound that is not set is not checked. A value not read yet (null) is not checked either: the OCR suggestion of
/// WATER-01 may be partial.
/// </summary>
public static class WaterThresholdEvaluator
{
    public const string PhName = "pH";
    public const string SalinityName = "Salinity";
    public const string TemperatureName = "Temperature";

    private const string SalinityUnit = "‰";
    private const string TemperatureUnit = "°C";

    public static ThresholdEvaluation Evaluate(decimal? ph, decimal? salinity, decimal? temperature, WaterThresholds thresholds)
    {
        var violations = new List<(string Metric, string Stored, WaterValueOutside Outside)>();
        Check(PhName, WaterValueOutside.Ph, null, ph, thresholds.PhMin, thresholds.PhMax, violations);
        Check(SalinityName, WaterValueOutside.Salinity, SalinityUnit, salinity, thresholds.SalinityMin, thresholds.SalinityMax, violations);
        Check(TemperatureName, WaterValueOutside.Temperature, TemperatureUnit, temperature, thresholds.TemperatureMin, thresholds.TemperatureMax, violations);

        return violations.Count == 0
            ? new ThresholdEvaluation(false, [], null, [])
            : new ThresholdEvaluation(
                true,
                violations.Select(v => v.Metric).ToList(),
                string.Join("; ", violations.Select(v => v.Stored)),
                violations.Select(v => v.Outside).ToList());
    }

    private static void Check(
        string metric, string indicator, string? unit, decimal? read, decimal? min, decimal? max,
        List<(string, string, WaterValueOutside)> violations)
    {
        if (read is not { } value)
        {
            return;
        }

        if (min is { } low && value < low)
        {
            violations.Add((metric, $"{metric} {Format(value)} < {Format(low)}", new(indicator, value, unit, WaterValueDirection.Low, min, max)));
        }
        else if (max is { } high && value > high)
        {
            violations.Add((metric, $"{metric} {Format(value)} > {Format(high)}", new(indicator, value, unit, WaterValueDirection.High, min, max)));
        }
    }

    private static string Format(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);
}
