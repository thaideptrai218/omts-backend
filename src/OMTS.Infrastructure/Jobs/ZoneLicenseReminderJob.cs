using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OMTS.Application.Features.Farming.Licenses;

namespace OMTS.Infrastructure.Jobs;

/// <summary>"Jobs:ZoneLicenseReminder" section.</summary>
public sealed class ZoneLicenseReminderOptions : IDailyJobOptions
{
    public const string SectionName = "Jobs:ZoneLicenseReminder";

    /// <summary>Daily run time in the business time zone (UTC+7).</summary>
    public TimeOnly RunAt { get; set; } = new(7, 0);

    /// <summary>
    /// A host that starts after today's run time (deploy, restart) runs once at start, so that day is not
    /// skipped. Safe because a notice already sent is never sent again.
    /// </summary>
    public bool CatchUpOnStart { get; set; } = true;
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
            candidates = await scope.ServiceProvider.GetRequiredService<ZoneLicenseReminderService>().CandidateIdsAsync(today, cancellationToken);
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

/// <summary>Runs <see cref="ZoneLicenseReminderJob"/> once a day at <see cref="ZoneLicenseReminderOptions.RunAt"/> (see <see cref="DailyJobHostedService"/>).</summary>
internal sealed class ZoneLicenseReminderHostedService(
    ZoneLicenseReminderJob job,
    IOptions<ZoneLicenseReminderOptions> options,
    TimeProvider timeProvider,
    ILogger<ZoneLicenseReminderHostedService> logger) : DailyJobHostedService(options.Value, timeProvider, logger)
{
    protected override string JobName => "Zone license reminder";

    protected override async Task RunOnceAsync(DateOnly today, CancellationToken cancellationToken)
    {
        var sent = await job.RunOnceAsync(today, cancellationToken);
        Logger.LogInformation("Zone license reminder sent {Count} notices.", sent);
    }
}
