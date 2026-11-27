using System.Text.Json;
using System.Text.Json.Serialization;

namespace OMTS.Domain.Enums;

/// <summary>
/// The input VAT rate (thuế GTGT đầu vào) of a purchase line: Kct (not subject to tax), Zero, Five, Eight or Ten percent. Stored by name; on the wire it is the string "KCT", "0", "5", "8" or "10"
/// (see <see cref="VatRates"/>). A line made before input VAT existed has no rate at all (null) and carries no VAT.
/// </summary>
[JsonConverter(typeof(VatRateJsonConverter))]
public enum VatRate
{
    Kct,
    Zero,
    Five,
    Eight,
    Ten,
}

/// <summary>The meaning of the rates: their percent, their text on the wire and the rate an item starts with.</summary>
public static class VatRates
{
    /// <summary>The percent of a rate; a missing rate, KCT and 0 are all 0.</summary>
    public static decimal Percent(VatRate? rate) => rate switch
    {
        VatRate.Five => 5m,
        VatRate.Eight => 8m,
        VatRate.Ten => 10m,
        _ => 0m,
    };

    /// <summary>"KCT", "0", "5", "8" or "10".</summary>
    public static string Text(VatRate rate) => rate switch
    {
        VatRate.Kct => "KCT",
        VatRate.Zero => "0",
        VatRate.Five => "5",
        VatRate.Eight => "8",
        VatRate.Ten => "10",
        _ => throw new ArgumentOutOfRangeException(nameof(rate), rate, "Not a VAT rate."),
    };

    /// <summary>Reads "KCT", "0", "5", "8" or "10" (a trailing "%" and letter case do not matter); false for anything else.</summary>
    public static bool TryParse(string? text, out VatRate rate)
    {
        var value = text?.Trim().TrimEnd('%').Trim();
        switch (value)
        {
            case "0":
                rate = VatRate.Zero;
                return true;
            case "5":
                rate = VatRate.Five;
                return true;
            case "8":
                rate = VatRate.Eight;
                return true;
            case "10":
                rate = VatRate.Ten;
                return true;
        }

        rate = VatRate.Kct;
        return string.Equals(value, "KCT", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The rate an item of the group starts with: seed is never bought with VAT (KCT, fixed); supplies start at 10%.</summary>
    public static VatRate DefaultFor(InputItemGroup group) => group == InputItemGroup.Seed ? VatRate.Kct : VatRate.Ten;
}

/// <summary>Writes and reads a <see cref="VatRate"/> as the string of <see cref="VatRates.Text"/>; anything else is a JSON error.</summary>
public sealed class VatRateJsonConverter : JsonConverter<VatRate>
{
    public override VatRate Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String && VatRates.TryParse(reader.GetString(), out var rate)
            ? rate
            : throw new JsonException("A VAT rate is one of \"KCT\", \"0\", \"5\", \"8\" or \"10\".");

    public override void Write(Utf8JsonWriter writer, VatRate value, JsonSerializerOptions options) => writer.WriteStringValue(VatRates.Text(value));

    public override VatRate ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => Read(ref reader, typeToConvert, options);

    public override void WriteAsPropertyName(Utf8JsonWriter writer, VatRate value, JsonSerializerOptions options) => writer.WritePropertyName(VatRates.Text(value));
}
