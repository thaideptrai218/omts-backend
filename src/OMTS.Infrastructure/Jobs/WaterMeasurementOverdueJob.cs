using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OMTS.Application.Features.WaterQuality;

namespace OMTS.Infrastructure.Jobs;

/// <summary>
/// Runs <see cref="WaterMeasurementOverdueReminder"/> once a day at <see cref="WaterMeasurementOverdueOptions.RunAt"/>
/// business local time, and once at start when the host starts after it (<see cref="DailyJobHostedService"/>). Each run
/// has its own scope. The reminder sends once per overdue period, so a catch-up, a second run on the same day or a
/// missed day never sends a reminder twice.
/// </summary>
internal sealed class WaterMeasurementOverdueJob(
    IServiceScopeFactory scopes,
    IOptions<WaterMeasurementOverdueOptions> options,
    TimeProvider timeProvider,
    ILogger<WaterMeasurementOverdueJob> logger) : DailyJobHostedService(options.Value, timeProvider, logger)
{
    protected override string JobName => "Overdue water measurement reminder";

    protected override async Task RunOnceAsync(DateOnly today, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var reminded = await scope.ServiceProvider.GetRequiredService<WaterMeasurementOverdueReminder>().RunOnceAsync(today, cancellationToken);
        Logger.LogInformation("Overdue water measurement reminder sent for {Count} zones", reminded);
    }
}
