using System.Globalization;
using System.Text.RegularExpressions;

namespace OMTS.Application.Features.Workforce;

/// <summary>Field rules shared by the labor use cases (LAB-02…LAB-09); each adds the invalid field name to the list.</summary>
public static partial class LaborInputRules
{
    public const int NotesLength = 500;
    public const int ReasonLength = 500;

    /// <summary>Trimmed text of 1..max characters; null and the field added when missing or too long.</summary>
    public static string? Required(string? value, int max, string field, List<string> invalid)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > max)
        {
            invalid.Add(field);
            return null;
        }

        return trimmed;
    }

    /// <summary>Trimmed text or null when blank; the field added when longer than max.</summary>
    public static string? Optional(string? value, int max, string field, List<string> invalid)
    {
        var trimmed = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (trimmed?.Length > max)
        {
            invalid.Add(field);
        }

        return trimmed;
    }

    /// <summary>Citizen ID: 9 or 12 digits.</summary>
    public static bool IsCitizenId(string value) => CitizenIdPattern().IsMatch(value);

    /// <summary>Dates in messages: dd/MM/yyyy (e.g. MSG49, MSG72).</summary>
    public static string Date(DateOnly day) => day.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    [GeneratedRegex(@"\A(\d{9}|\d{12})\z")]
    private static partial Regex CitizenIdPattern();
}
