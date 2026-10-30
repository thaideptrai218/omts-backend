using OMTS.Application.Features.WaterQuality.Models;
using System.Globalization;
using OMTS.Application.Features.WaterQuality.Dtos;

namespace OMTS.Application.Features.WaterQuality;

/// <summary>
/// ENV-01 alert rule: a value below its minimum or above its maximum is a violation; a value equal to a
/// bound is within it, and a bound that is not set is not checked.
/// </summary>
public static class WaterThresholdEvaluator
{
    public const string PhName = "pH";
    public const string SalinityName = "Salinity";

    public static ThresholdEvaluation Evaluate(decimal ph, decimal salinity, WaterThresholds thresholds)
    {
        var violations = new List<(string Metric, string Text)>();
        Check(PhName, ph, thresholds.PhMin, thresholds.PhMax, violations);
        Check(SalinityName, salinity, thresholds.SalinityMin, thresholds.SalinityMax, violations);

        return violations.Count == 0
            ? new ThresholdEvaluation(false, [], null)
            : new ThresholdEvaluation(true, violations.Select(v => v.Metric).ToList(), string.Join("; ", violations.Select(v => v.Text)));
    }

    private static void Check(string metric, decimal value, decimal? min, decimal? max, List<(string, string)> violations)
    {
        if (min is { } low && value < low)
        {
            violations.Add((metric, $"{metric} {Format(value)} < {Format(low)}"));
        }
        else if (max is { } high && value > high)
        {
            violations.Add((metric, $"{metric} {Format(value)} > {Format(high)}"));
        }
    }

    private static string Format(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);
}
