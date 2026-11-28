using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Features.WorkDayPlans;

namespace OMTS.Infrastructure.Jobs;

/// <summary>"Jobs:WorkDayPlanExpiry" section: the invitations to a work-day plan that were not answered before their deadline end as Expired.</summary>
public sealed class WorkDayPlanExpiryOptions
{
    public const string SectionName = "Jobs:WorkDayPlanExpiry";

    /// <summary>False stops the job.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How often the invitations are checked: every minute.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(1);
}

/// <summary>
/// Expires the invitations past their deadline, one plan at a time in a scope and unit of work of its own: a plan saved meanwhile (a confirmation, a removal) loses only its own save and is read again at once.
/// Any other failure is logged and the plan is checked again at the next run. Running it twice expires nothing more.
/// </summary>
public sealed class WorkDayPlanExpiryJob(IServiceScopeFactory scopes, ILogger<WorkDayPlanExpiryJob> logger)
{
    /// <summary>The number of invitations that ended.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Guid> planIds;
        using (var scope = scopes.CreateScope())
        {
            planIds = await scope.ServiceProvider.GetRequiredService<WorkDayPlanExpiryService>().OverduePlanIdsAsync(cancellationToken);
        }

        var expired = 0;
        foreach (var planId in planIds)
        {
            try
            {
                expired += await ExpireWithOneRetryAsync(planId, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Expiring the invitations of work-day plan {PlanId} failed; checked again at the next run.", planId);
            }
        }

        return expired;
    }

    private async Task<int> ExpireWithOneRetryAsync(Guid planId, CancellationToken cancellationToken)
    {
        try
        {
            return await ExpireInOwnScopeAsync(planId, cancellationToken);
        }
        catch (ConcurrencyConflictException)
        {
            logger.LogInformation("Work-day plan {PlanId} was saved during the expiry check; checked once more.", planId);
            return await ExpireInOwnScopeAsync(planId, cancellationToken);
        }
    }

    private async Task<int> ExpireInOwnScopeAsync(Guid planId, CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<WorkDayPlanExpiryService>().ExpirePlanAsync(planId, cancellationToken);
    }
}

/// <summary>Runs <see cref="WorkDayPlanExpiryJob"/> every <see cref="WorkDayPlanExpiryOptions.Interval"/>. A failed run is logged and the next one is an interval later; stopping the host ends the job quietly.</summary>
internal sealed class WorkDayPlanExpiryHostedService(WorkDayPlanExpiryJob job, IOptions<WorkDayPlanExpiryOptions> options, TimeProvider timeProvider, ILogger<WorkDayPlanExpiryHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        var interval = options.Value.Interval > TimeSpan.Zero ? options.Value.Interval : TimeSpan.FromMinutes(1);
        using var timer = new PeriodicTimer(interval, timeProvider);
        try
        {
            do
            {
                try
                {
                    var expired = await job.RunOnceAsync(stoppingToken);
                    if (expired > 0)
                    {
                        logger.LogInformation("Work-day plan invitations expired: {Expired}.", expired);
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogError(exception, "Work-day plan invitation expiry run failed.");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // The host is stopping.
        }
    }
}
