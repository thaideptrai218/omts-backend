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
