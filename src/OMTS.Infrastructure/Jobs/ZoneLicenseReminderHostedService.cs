using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OMTS.Application.Features.Farming.Licenses;

namespace OMTS.Infrastructure.Jobs;

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
