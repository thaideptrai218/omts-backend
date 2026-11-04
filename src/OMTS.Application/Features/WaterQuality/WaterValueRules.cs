namespace OMTS.Application.Features.WaterQuality;

/// <summary>
/// Field rules of WATER-01, WATER-02 and WATER-04 (BR-WATER-03): pH 0…14, salinity 0…50 ‰, temperature 0…45 °C,
/// at most 2 decimals, no future time.
/// </summary>
public static class WaterValueRules
{
    public const decimal MaxPh = 14m;
    public const decimal MaxSalinity = 50m;
    public const decimal MaxTemperature = 45m;
    public const int MaxReasonLength = 1000;

    /// <summary>Device clocks may run a little ahead of the server.</summary>
    public static readonly TimeSpan FutureTolerance = TimeSpan.FromMinutes(5);

    public const string PhField = "pH";
    public const string SalinityField = "Salinity";
    public const string TemperatureField = "Temperature";
    public const string MeasuredAtField = "Measured at";
    public const string ReasonField = "Reason";

    public static bool IsValidPh(decimal value) => InRange(value, MaxPh);

    public static bool IsValidSalinity(decimal value) => InRange(value, MaxSalinity);

    public static bool IsValidTemperature(decimal value) => InRange(value, MaxTemperature);

    public static bool IsValidMeasuredAt(DateTimeOffset measuredAt, DateTimeOffset now) => measuredAt <= now + FutureTolerance;

    /// <summary>Names the missing or invalid fields of a set of values: every value is required (WATER-01 step 8, WATER-02 step 4).</summary>
    public static List<string> InvalidFields(decimal? ph, decimal? salinity, decimal? temperature, DateTimeOffset? measuredAt, DateTimeOffset now)
    {
        var invalid = new List<string>();
        if (ph is not { } p || !IsValidPh(p))
        {
            invalid.Add(PhField);
        }

        if (salinity is not { } s || !IsValidSalinity(s))
        {
            invalid.Add(SalinityField);
        }

        if (temperature is not { } t || !IsValidTemperature(t))
        {
            invalid.Add(TemperatureField);
        }

        if (measuredAt is not { } at || !IsValidMeasuredAt(at, now))
        {
            invalid.Add(MeasuredAtField);
        }

        return invalid;
    }

    /// <summary>The trimmed reason, or null when it is empty or longer than 1000 characters.</summary>
    public static string? Reason(string? reason)
    {
        var trimmed = reason?.Trim();
        return string.IsNullOrEmpty(trimmed) || trimmed.Length > MaxReasonLength ? null : trimmed;
    }

    // decimal(4,2) / decimal(5,2): more decimals would be silently rounded by the database.
    private static bool InRange(decimal value, decimal max) => value >= 0 && value <= max && decimal.Round(value, 2) == value;
}
