using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OMTS.Application.Features.Files;

namespace OMTS.Infrastructure.Jobs;

/// <summary>Runs <see cref="OrphanFileCleanupJob"/> once a day at <see cref="OrphanFileCleanupOptions.RunAt"/>.</summary>
internal sealed class OrphanFileCleanupHostedService(
    OrphanFileCleanupJob job,
    IOptions<OrphanFileCleanupOptions> options,
    TimeProvider timeProvider,
    ILogger<OrphanFileCleanupHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // A missed day only delays the clean-up: no catch-up run at start.
        while (options.Value.Enabled && !stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(DailyRunTime.DelayUntilNext(timeProvider.GetUtcNow(), options.Value.RunAt), timeProvider, stoppingToken);
                var deleted = await job.RunOnceAsync(timeProvider.GetUtcNow(), stoppingToken);
                logger.LogInformation("Orphan file clean-up deleted {Count} files.", deleted);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Orphan file clean-up run failed.");
            }
        }
    }
}
