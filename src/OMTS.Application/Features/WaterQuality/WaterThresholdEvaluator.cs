using System.Globalization;
using OMTS.Application.Features.WaterQuality.Dtos;

namespace OMTS.Application.Features.WaterQuality;

/// <summary>Result of comparing confirmed values with a threshold set.</summary>
/// <param name="ViolatedMetrics">Indicator names, e.g. ["pH", "Salinity"].</param>
/// <param name="ViolatedValues">Stored text, e.g. "pH 9.10 &gt; 8.50; Salinity 12.00 &lt; 15.00".</param>
/// <param name="ValuesOutside">
/// <c>{values_outside}</c> of MSG-WATER-03: each value outside with its allowed range, e.g. "Salinity 12 ‰ is too low
/// (allowed 15–30 ‰)"; null when nothing is outside.
/// </param>
public sealed record ThresholdEvaluation(bool Exceeded, IReadOnlyList<string> ViolatedMetrics, string? ViolatedValues, string? ValuesOutside = null);

/// <summary>
/// ENV-01 alert rule: a value below its minimum or above its maximum is a violation; a value equal to a
/// bound is within it, and a bound that is not set is not checked.
/// </summary>
public static class WaterThresholdEvaluator
{
    public const string PhName = "pH";
    public const string SalinityName = "Salinity";

    private const string SalinityUnit = " ‰";

    public static ThresholdEvaluation Evaluate(decimal ph, decimal salinity, WaterThresholds thresholds)
    {
        var violations = new List<Violation>();
        Check(PhName, "", ph, thresholds.PhMin, thresholds.PhMax, violations);
        Check(SalinityName, SalinityUnit, salinity, thresholds.SalinityMin, thresholds.SalinityMax, violations);

        return violations.Count == 0
            ? new ThresholdEvaluation(false, [], null)
            : new ThresholdEvaluation(
                true,
                violations.Select(v => v.Metric).ToList(),
                string.Join("; ", violations.Select(v => v.Stored)),
                string.Join("; ", violations.Select(v => v.Described)));
    }

    private static void Check(string metric, string unit, decimal value, decimal? min, decimal? max, List<Violation> violations)
    {
        if (min is { } low && value < low)
        {
            violations.Add(new(metric, $"{metric} {Format(value)} < {Format(low)}", $"{metric} {Short(value)}{unit} is too low ({Allowed(min, max, unit)})"));
        }
        else if (max is { } high && value > high)
        {
            violations.Add(new(metric, $"{metric} {Format(value)} > {Format(high)}", $"{metric} {Short(value)}{unit} is too high ({Allowed(min, max, unit)})"));
        }
    }

    private static string Allowed(decimal? min, decimal? max, string unit) => (min, max) switch
    {
        ({ } low, { } high) => $"allowed {Short(low)}–{Short(high)}{unit}",
        ({ } low, null) => $"allowed at least {Short(low)}{unit}",
        (null, { } high) => $"allowed at most {Short(high)}{unit}",
        _ => "no range set",
    };

    private static string Format(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    // As the SRS example "Salinity 12 ‰": no trailing zeros.
    private static string Short(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private sealed record Violation(string Metric, string Stored, string Described);
}
