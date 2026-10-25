using System.Globalization;
using System.Text.RegularExpressions;
using OMTS.Application.Common;

namespace OMTS.Api.Common;

/// <summary>
/// Parses multipart form fields with the invariant culture (form model binding would use the server culture,
/// where "7.8" may not be a number). A time without an offset is read as business local time (UTC+7).
/// Each parser returns false only for a present but unreadable value.
/// </summary>
internal static partial class InvariantFormValues
{
    public static bool TryDecimal(string? value, out decimal? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (!decimal.TryParse(value.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
        {
            return false;
        }

        result = parsed;
        return true;
    }

    public static bool TryGuid(string? value, out Guid? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (!Guid.TryParse(value.Trim(), out var parsed))
        {
            return false;
        }

        result = parsed;
        return true;
    }

    public static bool TryBoolean(string? value, out bool result)
    {
        result = false;
        return string.IsNullOrWhiteSpace(value) || bool.TryParse(value.Trim(), out result);
    }

    public static bool TryDateTimeOffset(string? value, out DateTimeOffset? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var text = value.Trim();
        if (HasOffset().IsMatch(text))
        {
            if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var withOffset))
            {
                return false;
            }

            result = withOffset;
            return true;
        }

        if (!DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            return false;
        }

        result = BusinessClock.FromLocal(local);
        return true;
    }

    [GeneratedRegex(@"(Z|[+-]\d{2}:?\d{2})\z", RegexOptions.IgnoreCase)]
    private static partial Regex HasOffset();
}
