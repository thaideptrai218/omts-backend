using System.Globalization;
using OMTS.Application.Common.Messages;
using OMTS.Application.Features.WaterQuality.Dtos;

namespace OMTS.Application.Features.WaterQuality;

/// <summary>Result of comparing confirmed values with a threshold set.</summary>
/// <param name="ViolatedMetrics">Indicator names, e.g. ["pH", "Salinity"].</param>
/// <param name="ViolatedValues">Stored text, e.g. "pH 9.10 &gt; 8.50; Salinity 12.00 &lt; 15.00".</param>
/// <param name="ValuesOutside">The values outside with their allowed range, for MSG-WATER-03; empty when nothing is outside.</param>
public sealed record ThresholdEvaluation(
    bool Exceeded, IReadOnlyList<string> ViolatedMetrics, string? ViolatedValues, IReadOnlyList<WaterValueOutside> ValuesOutside);

/// <summary>
/// ENV-01 alert rule: a value below its minimum or above its maximum is a violation; a value equal to a
/// bound is within it, and a bound that is not set is not checked.
/// </summary>
public static class WaterThresholdEvaluator
{
    public const string PhName = "pH";
    public const string SalinityName = "Salinity";

    private const string SalinityUnit = "‰";

    public static ThresholdEvaluation Evaluate(decimal ph, decimal salinity, WaterThresholds thresholds)
    {
        var violations = new List<(string Metric, string Stored, WaterValueOutside Outside)>();
        Check(PhName, WaterValueOutside.Ph, null, ph, thresholds.PhMin, thresholds.PhMax, violations);
        Check(SalinityName, WaterValueOutside.Salinity, SalinityUnit, salinity, thresholds.SalinityMin, thresholds.SalinityMax, violations);

        return violations.Count == 0
            ? new ThresholdEvaluation(false, [], null, [])
            : new ThresholdEvaluation(
                true,
                violations.Select(v => v.Metric).ToList(),
                string.Join("; ", violations.Select(v => v.Stored)),
                violations.Select(v => v.Outside).ToList());
    }

    private static void Check(
        string metric, string indicator, string? unit, decimal value, decimal? min, decimal? max,
        List<(string, string, WaterValueOutside)> violations)
    {
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
