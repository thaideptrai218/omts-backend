using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OMTS.Application.Features.Cooperatives;
using OMTS.Application.Features.Farming.Licenses;

namespace OMTS.Infrastructure.Jobs;

/// <summary>"Jobs:ZoneLicenseReminder" section of the GFR-03 expiry reminder (zone licenses and cooperative certifications).</summary>
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
/// GFR-03 daily expiry check: the zone licenses (<see cref="ZoneLicenseReminderService"/>), then the cooperative
/// certifications (<see cref="CertificationReminderService"/>). Each record runs in its own scope and unit of work, so one
/// failing record is logged and skipped while the others continue. The name and the settings section keep the license
/// reminder's, so deployed settings stay valid.
/// </summary>
public sealed class ZoneLicenseReminderJob(IServiceScopeFactory scopes, ILogger<ZoneLicenseReminderJob> logger)
{
    /// <summary>Number of notices sent (licenses and certifications); running twice on the same day sends nothing new.</summary>
    public async Task<int> RunOnceAsync(DateOnly today, CancellationToken cancellationToken = default) =>
        await RemindEachAsync<ZoneLicenseReminderService>("license", s => s.CandidateIdsAsync(today, cancellationToken),
            (s, id) => s.RemindAsync(id, today, cancellationToken), cancellationToken)
        + await RemindEachAsync<CertificationReminderService>("certification", s => s.CandidateIdsAsync(today, cancellationToken),
            (s, id) => s.RemindAsync(id, today, cancellationToken), cancellationToken);

    private async Task<int> RemindEachAsync<TService>(
        string recordName, Func<TService, Task<IReadOnlyList<Guid>>> candidates, Func<TService, Guid, Task<bool>> remind, CancellationToken cancellationToken)
        where TService : notnull
    {
        IReadOnlyList<Guid> ids;
        using (var scope = scopes.CreateScope())
        {
            ids = await candidates(scope.ServiceProvider.GetRequiredService<TService>());
        }

        var sent = 0;
        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var scope = scopes.CreateScope();
                if (await remind(scope.ServiceProvider.GetRequiredService<TService>(), id))
                {
                    sent++;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Expiry reminder failed for {Record} {RecordId}; skipped.", recordName, id);
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
