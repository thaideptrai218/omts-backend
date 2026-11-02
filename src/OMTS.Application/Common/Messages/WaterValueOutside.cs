using System.Globalization;

namespace OMTS.Application.Common.Messages;

/// <summary>Whether a water value is above its maximum or below its minimum.</summary>
public enum WaterValueDirection
{
    High,
    Low,
}

/// <summary>
/// One value outside its thresholds: an item of the <c>values_outside</c> parameter of MSG-WATER-03, stored as atomic
/// values (indicator code, value, unit, direction and the allowed range) so a client renders it in its own language.
/// </summary>
/// <param name="Indicator"><see cref="Ph"/> or <see cref="Salinity"/>.</param>
/// <param name="Unit">"‰" for salinity, null for pH.</param>
/// <param name="Min">The minimum of the thresholds used, null when not set.</param>
/// <param name="Max">The maximum of the thresholds used, null when not set.</param>
public sealed record WaterValueOutside(string Indicator, decimal Value, string? Unit, WaterValueDirection Direction, decimal? Min, decimal? Max)
{
    public const string Ph = "PH";
    public const string Salinity = "SALINITY";

    /// <summary>The English phrase of the SRS example, e.g. "Salinity 12 ‰ is too low (allowed 15–30 ‰)".</summary>
    public string Describe()
    {
        var unit = Unit is null ? "" : " " + Unit;
        var name = Indicator == Ph ? "pH" : Indicator == Salinity ? "Salinity" : Indicator;
        var range = (Min, Max) switch
        {
            ({ } low, { } high) => $"allowed {Short(low)}–{Short(high)}{unit}",
            ({ } low, null) => $"allowed at least {Short(low)}{unit}",
            (null, { } high) => $"allowed at most {Short(high)}{unit}",
            _ => "no range set",
        };
        return $"{name} {Short(Value)}{unit} is too {(Direction == WaterValueDirection.High ? "high" : "low")} ({range})";
    }

    // As the SRS example "Salinity 12 ‰": no trailing zeros.
    private static string Short(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
