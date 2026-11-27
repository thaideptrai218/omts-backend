using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using OMTS.Application.Common;

namespace OMTS.Application.Features.Audit;

/// <summary>
/// A stored audit value as a reader of the history writes it: dates as dd/MM/yyyy, times in Vietnam as dd/MM/yyyy HH:mm, numbers with a decimal comma and a point between thousands,
/// Có/Không for a flag, coded values in Vietnamese, a reference to another record by its code or name, photos as a count. A value with nothing to show (empty, or a reference
/// that cannot be named) is null, so it is never printed as a raw id or as JSON.
/// </summary>
internal static partial class RecordHistoryValues
{
    private const string PhotosField = "Photos";

    private static readonly NumberFormatInfo Numbers = new() { NumberDecimalSeparator = ",", NumberGroupSeparator = ".", NumberGroupSizes = [3] };

    /// <summary>The ids found anywhere in a stored value: the ones <see cref="Format"/> will ask the references for.</summary>
    public static IEnumerable<Guid> ReferencesIn(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String when Guid.TryParseExact(value.GetString(), "D", out var id):
                yield return id;
                break;
            case JsonValueKind.Array:
                foreach (var id in value.EnumerateArray().SelectMany(ReferencesIn))
                {
                    yield return id;
                }

                break;
            case JsonValueKind.Object:
                foreach (var id in value.EnumerateObject().SelectMany(p => ReferencesIn(p.Value)))
                {
                    yield return id;
                }

                break;
        }
    }

    /// <param name="references">The code or name of the records the ids of the history point to.</param>
    public static string? Format(string recordType, string field, JsonElement value, IReadOnlyDictionary<Guid, string> references) => value.ValueKind switch
    {
        JsonValueKind.True => "Có",
        JsonValueKind.False => "Không",
        JsonValueKind.Number when field == PhotosField => Photos(value.GetDecimal()),
        JsonValueKind.Number => Number(value.GetDecimal()),
        JsonValueKind.String => OfString(recordType, field, value.GetString() ?? string.Empty, references),
        JsonValueKind.Array when field == PhotosField => Photos(value.GetArrayLength()),
        JsonValueKind.Array => Join(value.EnumerateArray().Select(item => Format(recordType, field, item, references))),
        JsonValueKind.Object => OfObject(value, references),
        _ => null,
    };

    /// <summary>"1.234,5": the decimal comma and the point between thousands of Vietnam, no trailing zeros, whatever the culture of the server.</summary>
    public static string Number(decimal value) => value.ToString("#,##0.###", Numbers);

    private static string? Photos(decimal count) => count > 0 ? $"{Number(count)} ảnh" : null;

    private static string? Join(IEnumerable<string?> parts)
    {
        var shown = parts.Where(p => !string.IsNullOrEmpty(p)).ToList();
        return shown.Count == 0 ? null : string.Join(", ", shown);
    }

    private static string? OfString(string recordType, string field, string text, IReadOnlyDictionary<Guid, string> references)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (Guid.TryParseExact(text, "D", out var id))
        {
            return references.GetValueOrDefault(id);
        }

        if (DateOnlyPattern().IsMatch(text) && DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
        }

        if (DateTimePattern().IsMatch(text) && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at))
        {
            return at.ToOffset(BusinessClock.UtcOffset).ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);
        }

        return RecordHistoryLabels.Value(recordType, field, text) ?? text;
    }

    /// <summary>
    /// A line of a list kept as an object: a product line (name, cages, kg of a weighing) or a trader told of a withdrawal (name and invoices). An object of another shape has nothing
    /// a reader can use, so it shows nothing rather than its JSON.
    /// </summary>
    private static string? OfObject(JsonElement item, IReadOnlyDictionary<Guid, string> references)
    {
        string? name = null;
        var figures = new List<string>();
        foreach (var property in item.EnumerateObject())
        {
            switch (property.Name)
            {
                case "ProductId":
                    name = property.Value.ValueKind == JsonValueKind.String && Guid.TryParseExact(property.Value.GetString(), "D", out var product) && references.TryGetValue(product, out var productName) ? productName : "Sản phẩm";
                    break;
                case "CustomerName" when property.Value.ValueKind == JsonValueKind.String:
                    name = property.Value.GetString();
                    break;
                case "CageCount" when property.Value.ValueKind == JsonValueKind.Number:
                    figures.Add($"{Number(property.Value.GetDecimal())} lồng");
                    break;
                case "Kg" or "WeighedKg" when property.Value.ValueKind == JsonValueKind.Number:
                    figures.Add($"{Number(property.Value.GetDecimal())} kg");
                    break;
                case "InvoiceCodes" when property.Value.ValueKind == JsonValueKind.Array:
                    if (Join(property.Value.EnumerateArray().Select(c => c.ValueKind == JsonValueKind.String ? c.GetString() : null)) is { } codes)
                    {
                        figures.Add(codes);
                    }

                    break;
            }
        }

        var detail = figures.Count > 0 ? string.Join(", ", figures) : null;
        return (name, detail) switch
        {
            (null, null) => null,
            (null, _) => detail,
            (_, null) => name,
            _ => $"{name} ({detail})",
        };
    }

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}$")]
    private static partial Regex DateOnlyPattern();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T")]
    private static partial Regex DateTimePattern();
}
