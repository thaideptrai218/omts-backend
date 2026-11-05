namespace OMTS.Application.Features.WaterQuality;

/// <summary>The water indicators as named in messages: {indicator_list} of MSG-WATER-01 and {indicator} of MSG-WATER-05.</summary>
public static class WaterIndicators
{
    public const string PhName = "pH";
    public const string SalinityName = "salinity";
    public const string TemperatureName = "temperature";

    public static readonly IReadOnlyList<string> All = [PhName, SalinityName, TemperatureName];

    /// <summary>"pH", "pH and salinity", or "pH, salinity and temperature".</summary>
    public static string Join(IReadOnlyList<string> names) =>
        names.Count <= 1 ? string.Concat(names) : $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}";
}
