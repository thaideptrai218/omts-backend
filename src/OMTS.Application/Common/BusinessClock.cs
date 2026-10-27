namespace OMTS.Application.Common;

/// <summary>
/// Business dates are Vietnamese local dates (Asia/Ho_Chi_Minh, UTC+7, no daylight saving): a grant
/// that ends today stops working at local midnight, not at 07:00.
/// </summary>
public static class BusinessClock
{
    public static readonly TimeSpan UtcOffset = TimeSpan.FromHours(7);

    public static DateOnly ToBusinessDate(DateTimeOffset instant) => DateOnly.FromDateTime(instant.ToOffset(UtcOffset).DateTime);

    /// <summary>
    /// A date and time sent without a UTC offset, read as business local time (UTC+7) and never as the server's time
    /// zone or UTC: "2026-10-20T08:00" is 08:00 in Vietnam (01:00Z). Every time the API reads uses this rule.
    /// </summary>
    public static DateTimeOffset FromLocal(DateTime local) => new(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), UtcOffset);

    /// <summary>
    /// <see cref="FromLocal"/> for a value a client sent: false for the first seven hours of year 1, which have no UTC
    /// instant (they would be before 0001-01-01T00:00Z), so the caller answers "invalid" (MSG01) instead of failing.
    /// </summary>
    public static bool TryFromLocal(DateTime local, out DateTimeOffset instant)
    {
        instant = local.Ticks >= UtcOffset.Ticks ? FromLocal(local) : default;
        return local.Ticks >= UtcOffset.Ticks;
    }

    /// <summary>Today's business date.</summary>
    public static DateOnly Today(this TimeProvider timeProvider) => ToBusinessDate(timeProvider.GetUtcNow());

    /// <summary>Local time as shown in messages, e.g. MSG06 "at 14:05 23/10/2026".</summary>
    public static string Format(DateTimeOffset instant) =>
        instant.ToOffset(UtcOffset).ToString("HH:mm dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture);
}
