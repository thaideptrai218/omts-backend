using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.WaterQuality.Dtos;

namespace OMTS.Application.Features.WaterQuality;

/// <summary>
/// ENV-04 input rules: each pair (pH, salinity) is set or cleared together, every value is in range
/// (MSG-GEN-01), and the minimum is less than the maximum (MSG-WATER-05).
/// </summary>
public static class WaterThresholdRules
{
    public const string PhMinField = "pH minimum";
    public const string PhMaxField = "pH maximum";
    public const string SalinityMinField = "Salinity minimum";
    public const string SalinityMaxField = "Salinity maximum";

    public static Result<WaterThresholds> Validate(SetWaterThresholdsRequest request)
    {
        var invalid = new List<string>();
        CheckPair(request.PhMin, request.PhMax, PhMinField, PhMaxField, WaterValueRules.IsValidPh, invalid);
        CheckPair(request.SalinityMin, request.SalinityMax, SalinityMinField, SalinityMaxField, WaterValueRules.IsValidSalinity, invalid);
        if (invalid.Count > 0)
        {
            return AppError.InvalidFields(invalid);
        }

        var notLess = new List<string>();
        if (request.PhMin >= request.PhMax)
        {
            notLess.Add(PhMinField);
        }

        if (request.SalinityMin >= request.SalinityMax)
        {
            notLess.Add(SalinityMinField);
        }

        if (notLess.Count > 0)
        {
            var indicator = notLess.Count == 2 ? "pH and salinity" : notLess[0] == PhMinField ? "pH" : "salinity";
            return new AppError(
                ErrorKind.Validation,
                UserMessages.MinimumNotLessThanMaximum(indicator),
                new Dictionary<string, object?> { ["field"] = notLess[0], ["fields"] = notLess });
        }

        return new WaterThresholds(request.PhMin, request.PhMax, request.SalinityMin, request.SalinityMax);
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
