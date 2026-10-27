using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OMTS.Application.Features.Files;

namespace OMTS.Infrastructure.Jobs;

/// <summary>"Jobs:OrphanFileCleanup" section.</summary>
public sealed class OrphanFileCleanupOptions
{
    public const string SectionName = "Jobs:OrphanFileCleanup";

    /// <summary>The shortest grace period accepted: an upload and its save never take that long.</summary>
    public static readonly TimeSpan MinimumGracePeriod = TimeSpan.FromHours(1);

    /// <summary>False stops the daily run; <see cref="OrphanFileCleanupJob.RunOnceAsync"/> still works when called.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Daily run time in the business time zone (UTC+7), outside working hours.</summary>
    public TimeOnly RunAt { get; set; } = new(3, 0);

    /// <summary>A file written less than this long ago is kept, so a file uploaded right now is never deleted.</summary>
    public TimeSpan GracePeriod { get; set; } = TimeSpan.FromHours(24);
}

/// <summary>
/// Deletes raft photo and license files that no record points to (<see cref="OrphanFileCleanupService"/>). Each
/// category runs in its own scope, so a failing one is logged and skipped while the other continues.
/// </summary>
public sealed class OrphanFileCleanupJob(IServiceScopeFactory scopes, IOptions<OrphanFileCleanupOptions> options, ILogger<OrphanFileCleanupJob> logger)
{
    /// <summary>Number of files deleted; files written within the grace period before <paramref name="now"/> are kept.</summary>
    public async Task<int> RunOnceAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var deleted = 0;
        foreach (var category in OrphanFileCleanupService.Categories)
        {
            try
            {
                using var scope = scopes.CreateScope();
                deleted += await scope.ServiceProvider.GetRequiredService<OrphanFileCleanupService>()
                    .CleanAsync(category, now - options.Value.GracePeriod, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Orphan file clean-up of {Category} failed; skipped.", category);
            }
        }

        return deleted;
    }
}

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
