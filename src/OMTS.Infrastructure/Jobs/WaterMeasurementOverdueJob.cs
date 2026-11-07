using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OMTS.Application.Features.WaterQuality;

namespace OMTS.Infrastructure.Jobs;

/// <summary>
/// GFR-04 overdue water measurement reminder. Each zone runs in its own scope and unit of work, so a zone saved during the
/// run (or failing in any other way) is logged and skipped while the others continue; it is tried again at the next run. A
/// reminder and the zone's marker are saved together, so a catch-up, a second run on the same day or a retry never sends it twice.
/// </summary>
public sealed class WaterMeasurementOverdueJob(IServiceScopeFactory scopes, ILogger<WaterMeasurementOverdueJob> logger)
{
    /// <summary>Number of zones reminded; running twice on the same day sends nothing new.</summary>
    public async Task<int> RunOnceAsync(DateOnly today, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Guid> candidates;
        using (var scope = scopes.CreateScope())
        {
            candidates = await scope.ServiceProvider.GetRequiredService<WaterMeasurementOverdueReminder>().CandidateZoneIdsAsync(today, cancellationToken);
        }

        var reminded = 0;
        foreach (var zoneId in candidates)
        {
            try
            {
                using var scope = scopes.CreateScope();
                if (await scope.ServiceProvider.GetRequiredService<WaterMeasurementOverdueReminder>().RemindAsync(zoneId, today, cancellationToken))
                {
                    reminded++;
                }
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogError(exception, "Overdue water measurement reminder failed for zone {ZoneId}; skipped.", zoneId);
            }
        }

        return reminded;
    }
}

/// <summary>
/// Runs <see cref="WaterMeasurementOverdueJob"/> once a day at <see cref="WaterMeasurementOverdueOptions.RunAt"/> business
/// local time, and once at start when the host starts after it (see <see cref="DailyJobHostedService"/>).
/// </summary>
internal sealed class WaterMeasurementOverdueHostedService(
    WaterMeasurementOverdueJob job,
    IOptions<WaterMeasurementOverdueOptions> options,
    TimeProvider timeProvider,
    ILogger<WaterMeasurementOverdueHostedService> logger) : DailyJobHostedService(options.Value, timeProvider, logger)
{
    protected override string JobName => "Overdue water measurement reminder";

    protected override async Task RunOnceAsync(DateOnly today, CancellationToken cancellationToken)
    {
        var reminded = await job.RunOnceAsync(today, cancellationToken);
        Logger.LogInformation("Overdue water measurement reminder sent for {Count} zones", reminded);
    }
}
