namespace OMTS.Application.Common;

/// <summary>
/// Business dates are Vietnamese local dates (Asia/Ho_Chi_Minh, UTC+7, no daylight saving): a grant
/// that ends today stops working at local midnight, not at 07:00.
/// </summary>
public static class BusinessClock
{
    public static readonly TimeSpan UtcOffset = TimeSpan.FromHours(7);

    public static DateOnly ToBusinessDate(DateTimeOffset instant) => DateOnly.FromDateTime(instant.ToOffset(UtcOffset).DateTime);

    /// <summary>Today's business date.</summary>
    public static DateOnly Today(this TimeProvider timeProvider) => ToBusinessDate(timeProvider.GetUtcNow());

    /// <summary>Local time as shown in messages, e.g. MSG06 "at 14:05 23/10/2026".</summary>
    public static string Format(DateTimeOffset instant) =>
        instant.ToOffset(UtcOffset).ToString("HH:mm dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture);
}
