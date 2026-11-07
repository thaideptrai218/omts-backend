using System.Globalization;

namespace OMTS.Api.Common;

/// <summary>Numbers read from multipart form fields (HARVEST-01, HARVEST-01.1, TRACE-03.2), in the invariant culture.</summary>
internal static class FormNumbers
{
    /// <summary>The numbers sent; empty when none was; null when one cannot be read, so it is refused, not dropped.</summary>
    public static List<int>? Numbers(string?[]? values)
    {
        var numbers = new List<int>(values?.Length ?? 0);
        foreach (var value in values ?? [])
        {
            if (string.IsNullOrWhiteSpace(value) || !TryInt(value, out var number))
            {
                return null;
            }

            numbers.Add(number!.Value);
        }

        return numbers;
    }

    /// <summary>An integer in the invariant culture; a blank value is read as none.</summary>
    public static bool TryInt(string? value, out int? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (!int.TryParse(value.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed))
        {
            return false;
        }

        result = parsed;
        return true;
    }

    /// <summary>
    /// A number with a dot; a blank value is read as none. No thousands separator: "400,5" typed with a decimal comma would
    /// otherwise be read as 4005, ten times too large.
    /// </summary>
    public static bool TryDecimal(string? value, out decimal? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (!decimal.TryParse(value.Trim(), NumberStyles.Number & ~NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var parsed))
        {
            return false;
        }

        result = parsed;
        return true;
    }
}
