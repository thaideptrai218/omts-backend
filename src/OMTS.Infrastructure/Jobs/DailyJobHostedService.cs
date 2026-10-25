using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OMTS.Application.Common;

namespace OMTS.Infrastructure.Jobs;

/// <summary>
/// Runs a job once a day at <see cref="IDailyJobOptions.RunAt"/> business local time (UTC+7), shared by the zone
/// license reminder and the overdue water measurement reminder. With <see cref="IDailyJobOptions.CatchUpOnStart"/> a
/// host started after today's run time runs it once at start. A failed run is logged and the next one is tomorrow;
/// the jobs send each notice once per period, so a catch-up or a missed day never sends anything twice.
/// </summary>
internal abstract class DailyJobHostedService(IDailyJobOptions options, TimeProvider timeProvider, ILogger logger) : BackgroundService
{
    /// <summary>Name in the log lines, e.g. "Zone license reminder".</summary>
    protected abstract string JobName { get; }

    protected ILogger Logger => logger;

    /// <summary>One run for the business date <paramref name="today"/>.</summary>
    protected abstract Task RunOnceAsync(DateOnly today, CancellationToken cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var catchUp = options.CatchUpOnStart && IsPastRunTime(timeProvider.GetUtcNow(), options.RunAt);
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!catchUp)
            {
                await Task.Delay(DelayUntilNextRun(timeProvider.GetUtcNow(), options.RunAt), timeProvider, stoppingToken);
            }

            catchUp = false;
            try
            {
                await RunOnceAsync(timeProvider.Today(), stoppingToken);
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(exception, "{Job} run failed.", JobName);
            }
        }
    }

    internal static bool IsPastRunTime(DateTimeOffset nowUtc, TimeOnly runAt) =>
        TimeOnly.FromTimeSpan(nowUtc.ToOffset(BusinessClock.UtcOffset).TimeOfDay) >= runAt;

    /// <summary>Time until the next <paramref name="runAt"/> in business local time (tomorrow when it has passed today).</summary>
    internal static TimeSpan DelayUntilNextRun(DateTimeOffset nowUtc, TimeOnly runAt)
    {
        var local = nowUtc.ToOffset(BusinessClock.UtcOffset);
        var next = new DateTimeOffset(local.Date + runAt.ToTimeSpan(), BusinessClock.UtcOffset);
        return (next > local ? next : next.AddDays(1)) - local;
    }
}
