using System.Globalization;
using OMTS.Application.Common;

namespace OMTS.Api.Common;

/// <summary>
/// Parses multipart form fields with the invariant culture (form model binding would use the server culture,
/// where "7.8" may not be a number). Times are ISO 8601 only, as in a JSON body; a time without an offset is read
/// as business local time (UTC+7). Each parser returns false only for a present but unreadable value.
/// </summary>
internal static class InvariantFormValues
{
    // The ISO 8601 forms a JSON body accepts: a date, or a date and time to the minute, the second or 1–7 fraction digits.
    private static readonly string[] LocalTimeFormats =
    [
        "yyyy'-'MM'-'dd",
        .. new[] { "HH':'mm", "HH':'mm':'ss" }.Concat(Enumerable.Range(1, 7).Select(digits => "HH':'mm':'ss'.'" + new string('f', digits)))
            .Select(time => "yyyy'-'MM'-'dd'T'" + time),
    ];

    // The same date and times followed by "Z" or "±hh:mm".
    private static readonly string[] OffsetTimeFormats =
        LocalTimeFormats.Skip(1).SelectMany(format => new[] { format + "'Z'", format + "zzz" }).ToArray();

    // A number with a dot. No thousands separator: "400,5" typed with a decimal comma would be read as 4005, ten times too large.
    private const NumberStyles DecimalStyle = NumberStyles.Number & ~NumberStyles.AllowThousands;

    public static bool TryDecimal(string? value, out decimal? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (!decimal.TryParse(value.Trim(), DecimalStyle, CultureInfo.InvariantCulture, out var parsed))
        {
            return false;
        }

        result = parsed;
        return true;
    }

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

    /// <summary>A date as in a JSON body, <c>yyyy-MM-dd</c>.</summary>
    public static bool TryDate(string? value, out DateOnly? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (!DateOnly.TryParseExact(value.Trim(), "yyyy'-'MM'-'dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return false;
        }

        result = parsed;
        return true;
    }

    public static bool TryDateTimeOffset(string? value, out DateTimeOffset? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var text = value.Trim();
        if (DateTime.TryParseExact(text, LocalTimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            // The first seven hours of year 1 have no UTC instant: unreadable, like in JSON.
            if (!BusinessClock.TryFromLocal(local, out var instant))
            {
                return false;
            }

            result = instant;
            return true;
        }

        // "Z" alone gives no offset to the parser: AssumeUniversal reads it as +00:00; "±hh:mm" is kept as sent.
        if (!DateTimeOffset.TryParseExact(text, OffsetTimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var withOffset))
        {
            return false;
        }

        result = withOffset;
        return true;
    }
}
