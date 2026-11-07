using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.WaterQuality.Dtos;

namespace OMTS.Application.Features.WaterQuality;

/// <summary>
/// WATER-04 input rules: each pair (pH, salinity, temperature) is set or cleared together, every value is in range
/// (MSG-GEN-01), and the minimum is less than the maximum (MSG-WATER-05 names the indicators to fix).
/// </summary>
public static class WaterThresholdRules
{
    public const string PhMinField = "pH minimum";
    public const string PhMaxField = "pH maximum";
    public const string SalinityMinField = "Salinity minimum";
    public const string SalinityMaxField = "Salinity maximum";
    public const string TemperatureMinField = "Temperature minimum";
    public const string TemperatureMaxField = "Temperature maximum";

    public static Result<WaterThresholds> Validate(SetWaterThresholdsRequest request)
    {
        var invalid = new List<string>();
        CheckPair(request.PhMin, request.PhMax, PhMinField, PhMaxField, WaterValueRules.IsValidPh, invalid);
        CheckPair(request.SalinityMin, request.SalinityMax, SalinityMinField, SalinityMaxField, WaterValueRules.IsValidSalinity, invalid);
        CheckPair(request.TemperatureMin, request.TemperatureMax, TemperatureMinField, TemperatureMaxField, WaterValueRules.IsValidTemperature, invalid);
        if (invalid.Count > 0)
        {
            return AppError.InvalidFields(invalid);
        }

        var notLess = new List<(string Field, string Indicator)>();
        if (request.PhMin >= request.PhMax)
        {
            notLess.Add((PhMinField, WaterIndicators.PhName));
        }

        if (request.SalinityMin >= request.SalinityMax)
        {
            notLess.Add((SalinityMinField, WaterIndicators.SalinityName));
        }

        if (request.TemperatureMin >= request.TemperatureMax)
        {
            notLess.Add((TemperatureMinField, WaterIndicators.TemperatureName));
        }

        if (notLess.Count > 0)
        {
            var fields = notLess.Select(n => n.Field).ToList();
            return new AppError(
                ErrorKind.Validation,
                UserMessages.MinimumNotLessThanMaximum(WaterIndicators.Join(notLess.Select(n => n.Indicator).ToList())),
                new Dictionary<string, object?> { ["field"] = fields[0], ["fields"] = fields });
        }

        return new WaterThresholds(request.PhMin, request.PhMax, request.SalinityMin, request.SalinityMax, request.TemperatureMin, request.TemperatureMax);
    }

    private static void CheckPair(decimal? min, decimal? max, string minField, string maxField, Func<decimal, bool> isValid, List<string> invalid)
    {
        if (min is { } low ? !isValid(low) : max is not null)
        {
            invalid.Add(minField);
        }

        if (max is { } high ? !isValid(high) : min is not null)
        {
            invalid.Add(maxField);
        }
    }
}
