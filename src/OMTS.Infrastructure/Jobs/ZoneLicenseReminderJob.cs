using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OMTS.Application.Common;
using OMTS.Application.Features.Farming.Licenses;

namespace OMTS.Infrastructure.Jobs;

/// <summary>"Jobs:ZoneLicenseReminder" section.</summary>
public sealed class ZoneLicenseReminderOptions
{
    public const string SectionName = "Jobs:ZoneLicenseReminder";

    /// <summary>Daily run time in the business time zone (UTC+7).</summary>
    public TimeOnly RunAt { get; set; } = new(7, 0);
}

/// <summary>
/// FARM-03 expiring license reminder. Each license runs in its own scope and unit of work, so one failing
/// license is logged and skipped while the others continue.
/// </summary>
public sealed class ZoneLicenseReminderJob(IServiceScopeFactory scopes, ILogger<ZoneLicenseReminderJob> logger)
{
    /// <summary>Number of notices sent; running twice on the same day sends nothing new.</summary>
    public async Task<int> RunOnceAsync(DateOnly today, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Guid> candidates;
        using (var scope = scopes.CreateScope())
        {
            candidates = await scope.ServiceProvider.GetRequiredService<ZoneLicenseReminderService>().CandidateIdsAsync(cancellationToken);
        }

        var sent = 0;
        foreach (var documentId in candidates)
        {
            try
            {
                using var scope = scopes.CreateScope();
                if (await scope.ServiceProvider.GetRequiredService<ZoneLicenseReminderService>().RemindAsync(documentId, today, cancellationToken))
                {
                    sent++;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Zone license reminder failed for license {LicenseId}; skipped.", documentId);
            }
        }

        return sent;
    }
}

/// <summary>Runs <see cref="ZoneLicenseReminderJob"/> once a day at <see cref="ZoneLicenseReminderOptions.RunAt"/>.</summary>
internal sealed class ZoneLicenseReminderHostedService(
    ZoneLicenseReminderJob job,
    IOptions<ZoneLicenseReminderOptions> options,
    TimeProvider timeProvider,
    ILogger<ZoneLicenseReminderHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(DelayUntilNextRun(timeProvider.GetUtcNow(), options.Value.RunAt), timeProvider, stoppingToken);
            try
            {
                var sent = await job.RunOnceAsync(timeProvider.Today(), stoppingToken);
                logger.LogInformation("Zone license reminder sent {Count} notices.", sent);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Zone license reminder run failed.");
            }
        }
    }

    internal static TimeSpan DelayUntilNextRun(DateTimeOffset nowUtc, TimeOnly runAt)
    {
        var local = nowUtc.ToOffset(BusinessClock.UtcOffset);
        var next = new DateTimeOffset(local.Date + runAt.ToTimeSpan(), BusinessClock.UtcOffset);
        return (next > local ? next : next.AddDays(1)) - local;
    }
}
