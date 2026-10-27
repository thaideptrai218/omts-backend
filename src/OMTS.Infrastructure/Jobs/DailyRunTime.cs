using OMTS.Application.Common;

namespace OMTS.Infrastructure.Jobs;

/// <summary>Daily run time of the orphan file clean-up, in the business time zone (UTC+7).</summary>
internal static class DailyRunTime
{
    /// <summary>Time until the next <paramref name="runAt"/> (tomorrow when it has passed today).</summary>
    public static TimeSpan DelayUntilNext(DateTimeOffset nowUtc, TimeOnly runAt)
    {
        var local = nowUtc.ToOffset(BusinessClock.UtcOffset);
        var next = new DateTimeOffset(local.Date + runAt.ToTimeSpan(), BusinessClock.UtcOffset);
        return (next > local ? next : next.AddDays(1)) - local;
    }
}
