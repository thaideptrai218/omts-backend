using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OMTS.Application.Common;
using OMTS.Application.Features.WaterQuality;

namespace OMTS.Infrastructure.Jobs;

/// <summary>
/// Runs <see cref="WaterMeasurementOverdueReminder"/> once a day at <see cref="RunAt"/> business local time.
/// A failed run is logged and retried the next day; the reminder itself sends once per overdue period, so a
/// missed day only delays it.
/// </summary>
internal sealed class WaterMeasurementOverdueJob(
    IServiceScopeFactory scopes,
    TimeProvider timeProvider,
    ILogger<WaterMeasurementOverdueJob> logger) : BackgroundService
{
    public static readonly TimeOnly RunAt = new(6, 0);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(DelayUntilNextRun(timeProvider.GetUtcNow()), timeProvider, stoppingToken);
                await using var scope = scopes.CreateAsyncScope();
                var reminded = await scope.ServiceProvider.GetRequiredService<WaterMeasurementOverdueReminder>()
                    .RunOnceAsync(timeProvider.Today(), stoppingToken);
                logger.LogInformation("Overdue water measurement reminder sent for {Count} zones", reminded);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Overdue water measurement reminder failed");
            }
        }
    }

    /// <summary>Time until the next <see cref="RunAt"/> in business local time (tomorrow when it has passed today).</summary>
    internal static TimeSpan DelayUntilNextRun(DateTimeOffset now)
    {
        var local = now.ToOffset(BusinessClock.UtcOffset);
        var next = new DateTimeOffset(DateOnly.FromDateTime(local.DateTime).ToDateTime(RunAt), BusinessClock.UtcOffset);
        if (next <= local)
        {
            next = next.AddDays(1);
        }

        return next - now;
    }
}
