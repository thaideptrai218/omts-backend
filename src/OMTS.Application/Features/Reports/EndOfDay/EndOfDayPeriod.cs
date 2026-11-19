using System.Globalization;
using OMTS.Application.Common;
using OMTS.Application.Common.Results;

namespace OMTS.Application.Features.Reports.EndOfDay;

/// <summary>
/// The period of the end-of-day report in Vietnam local time, both ends included: <see cref="To"/> is the last minute of the period. Asked for as <c>yyyy-MM-ddTHH:mm</c>; today from 00:00 to
/// 23:59 when none is given, the whole of <c>date</c> for the older way to ask. At most <see cref="MaxDays"/> days; an end before the start is MSG-GEN-01 naming "to".
/// </summary>
public sealed record EndOfDayPeriod(DateTime From, DateTime To)
{
    public const string Format = "yyyy-MM-ddTHH:mm";
    public const int MaxDays = 366;
    public const string FromField = "from";
    public const string ToField = "to";

    private static readonly string[] Formats = [Format, "yyyy-MM-ddTHH:mm:ss"];
    private static readonly DateTime Latest = new(9998, 12, 31, 23, 59, 0);

    public DateTimeOffset FromInstant => BusinessClock.FromLocal(From);

    /// <summary>The first instant after the period: the end minute is included.</summary>
    public DateTimeOffset ToExclusive => BusinessClock.FromLocal(To).AddMinutes(1);

    public DateOnly FromDate => DateOnly.FromDateTime(From);

    public DateOnly ToDate => DateOnly.FromDateTime(To);

    /// <summary>True when the period reaches over more than one calendar day, so a time of day is not enough to place a document.</summary>
    public bool SpansDays => To.Date > From.Date;

    public string FromText => From.ToString(Format, CultureInfo.InvariantCulture);

    public string ToText => To.ToString(Format, CultureInfo.InvariantCulture);

    public static Result<EndOfDayPeriod> Resolve(DateOnly? date, string? from, string? to, DateOnly today)
    {
        var hasFrom = !string.IsNullOrWhiteSpace(from);
        var hasTo = !string.IsNullOrWhiteSpace(to);
        DateTime start;
        DateTime end;
        if (!hasFrom && !hasTo)
        {
            var day = (date ?? today).ToDateTime(TimeOnly.MinValue);
            return Check(day, day.AddHours(23).AddMinutes(59));
        }

        if (hasFrom && !TryParse(from!, out start))
        {
            return AppError.Invalid(FromField);
        }

        if (hasTo && !TryParse(to!, out end))
        {
            return AppError.Invalid(ToField);
        }

        // Only one end given: the other is the start or the end of the same day.
        start = hasFrom ? Parse(from!) : Parse(to!).Date;
        end = hasTo ? Parse(to!) : start.Date.AddHours(23).AddMinutes(59);
        return Check(start, end);
    }

    private static Result<EndOfDayPeriod> Check(DateTime start, DateTime end)
    {
        if (!BusinessClock.TryFromLocal(start, out _))
        {
            return AppError.Invalid(FromField);
        }

        if (end < start || (end - start).TotalDays > MaxDays || end > Latest)
        {
            return AppError.Invalid(ToField);
        }

        return new EndOfDayPeriod(start, end);
    }

    private static DateTime Parse(string value) => DateTime.ParseExact(value.Trim(), Formats, CultureInfo.InvariantCulture, DateTimeStyles.None);

    private static bool TryParse(string value, out DateTime result) =>
        DateTime.TryParseExact(value.Trim(), Formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out result);
}
