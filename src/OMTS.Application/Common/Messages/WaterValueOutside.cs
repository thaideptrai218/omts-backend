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
/// values (indicator code, value, unit, direction and the allowed range) so a client can render it in its own way.
/// </summary>
/// <param name="Indicator"><see cref="Ph"/>, <see cref="Salinity"/> or <see cref="Temperature"/>.</param>
/// <param name="Unit">"‰" for salinity, "°C" for temperature, null for pH.</param>
/// <param name="Min">The minimum of the thresholds used, null when not set.</param>
/// <param name="Max">The maximum of the thresholds used, null when not set.</param>
public sealed record WaterValueOutside(string Indicator, decimal Value, string? Unit, WaterValueDirection Direction, decimal? Min, decimal? Max)
{
    public const string Ph = "PH";
    public const string Salinity = "SALINITY";
    public const string Temperature = "TEMPERATURE";

    /// <summary>
    /// The Vietnamese phrase shown to a user, with the limit that was crossed: "Độ mặn 10 ‰ thấp hơn mức cho phép 18 ‰",
    /// "pH 14 cao hơn mức cho phép 8,5". Numbers have a decimal comma and no trailing zeros.
    /// </summary>
    public string Describe() => Phrase(" mức cho phép");

    /// <summary>The same without "mức cho phép", for a text that has little room (an SMS): "pH 14 cao hơn 8,5".</summary>
    public string DescribeBrief() => Phrase(string.Empty);

    private string Phrase(string limitWords)
    {
        var unit = Unit is null ? "" : " " + Unit;
        var name = Indicator switch
        {
            Ph => "pH",
            Salinity => "Độ mặn",
            Temperature => "Nhiệt độ",
            _ => Indicator,
        };
        var high = Direction == WaterValueDirection.High;
        var phrase = $"{name} {Short(Value)}{unit} {(high ? "cao hơn" : "thấp hơn")}{limitWords}";
        return (high ? Max : Min) is { } limit ? $"{phrase} {Short(limit)}{unit}" : phrase;
    }

    // Vietnamese numbers: decimal comma, no trailing zeros ("8,5", "14"). The culture is fixed so the text does not depend on the server's locale.
    private static string Short(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture).Replace('.', ',');
}
