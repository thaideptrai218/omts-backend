namespace OMTS.Application.Features.WaterQuality;

/// <summary>Field rules of ENV-01/ENV-02/ENV-04: pH 0.00…14.00, salinity 0.00…100.00 ‰, at most 2 decimals, no future time.</summary>
public static class WaterValueRules
{
    public const decimal MaxPh = 14m;
    public const decimal MaxSalinity = 100m;
    public const int MaxReasonLength = 1000;

    /// <summary>Device clocks may run a little ahead of the server.</summary>
    public static readonly TimeSpan FutureTolerance = TimeSpan.FromMinutes(5);

    public const string PhField = "pH";
    public const string SalinityField = "Salinity";
    public const string MeasuredAtField = "Measured at";
    public const string ReasonField = "Reason";
    public const string RecordVersionField = "Record version";

    public static bool IsValidPh(decimal value) => InRange(value, MaxPh);

    public static bool IsValidSalinity(decimal value) => InRange(value, MaxSalinity);

    public static bool IsValidMeasuredAt(DateTimeOffset measuredAt, DateTimeOffset now) => measuredAt <= now + FutureTolerance;

    /// <summary>Names the invalid fields of a set of values; <paramref name="required"/> makes missing values invalid too.</summary>
    public static List<string> InvalidFields(decimal? ph, decimal? salinity, DateTimeOffset? measuredAt, DateTimeOffset now, bool required)
    {
        var invalid = new List<string>();
        if (ph is { } p ? !IsValidPh(p) : required)
        {
            invalid.Add(PhField);
        }

        if (salinity is { } s ? !IsValidSalinity(s) : required)
        {
            invalid.Add(SalinityField);
        }

        if (measuredAt is { } at ? !IsValidMeasuredAt(at, now) : required)
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
