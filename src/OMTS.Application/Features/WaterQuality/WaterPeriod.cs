using System.Globalization;
using OMTS.Application.Common;
using OMTS.Application.Common.Results;

namespace OMTS.Application.Features.WaterQuality;

/// <summary>
/// A range of business dates (Asia/Ho_Chi_Minh), both ends included, as used by the ENV-03 filters
/// (<c>from</c>/<c>to</c> in yyyy-MM-dd).
/// </summary>
public sealed record WaterPeriod(DateOnly From, DateOnly To)
{
    public const string FromField = "From";
    public const string ToField = "To";

    /// <summary>Start of <see cref="From"/> in local time.</summary>
    public DateTimeOffset FromInstant => StartOf(From);

    /// <summary>Start of the day after <see cref="To"/> in local time.</summary>
    public DateTimeOffset ToExclusive => StartOf(To.AddDays(1));

    public int Days => To.DayNumber - From.DayNumber + 1;

    /// <summary><c>to</c> defaults to today and <c>from</c> to <paramref name="defaultFrom"/>(to); from after to is invalid (MSG01).</summary>
    public static Result<WaterPeriod> Parse(string? from, string? to, DateOnly today, Func<DateOnly, DateOnly> defaultFrom)
    {
        if (!TryParse(to, out var parsedTo))
        {
            return AppError.Invalid(ToField);
        }

        if (!TryParse(from, out var parsedFrom))
        {
            return AppError.Invalid(FromField);
        }

        var end = parsedTo ?? today;
        var start = parsedFrom ?? defaultFrom(end);
        return start > end ? AppError.Invalid(FromField, "From must not be after To.") : new WaterPeriod(start, end);
    }

    private static DateTimeOffset StartOf(DateOnly date) => new(date.ToDateTime(TimeOnly.MinValue), BusinessClock.UtcOffset);

    private static bool TryParse(string? value, out DateOnly? date)
    {
        date = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (!DateOnly.TryParseExact(value.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return false;
        }

        date = parsed;
        return true;
    }
}
