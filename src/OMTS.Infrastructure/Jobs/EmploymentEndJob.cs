using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Features.Employees;

namespace OMTS.Infrastructure.Jobs;

/// <summary>"Jobs:EmploymentEnd" section.</summary>
public sealed class EmploymentEndOptions : IDailyJobOptions
{
    public const string SectionName = "Jobs:EmploymentEnd";

    /// <summary>Daily run time in the business time zone (UTC+7): early, as the employee is Left from the start of the day.</summary>
    public TimeOnly RunAt { get; set; } = new(0, 1);

    /// <summary>A host that starts after today's run time runs once at start. Safe because only those still Employed are changed.</summary>
    public bool CatchUpOnStart { get; set; } = true;
}

/// <summary>
/// BR-LABOR-01: an employee is Left from the day after the last working day. Each employee runs in its own scope and unit of
/// work, so one failing employee is logged and tried again on the next run while the others continue. A save that loses to an
/// edit of that employee is tried once more at once: waiting for the next run would cost a day.
/// </summary>
public sealed class EmploymentEndJob(IServiceScopeFactory scopes, ILogger<EmploymentEndJob> logger)
{
    /// <summary>Number of employees who became Left; running twice on the same day changes nothing more.</summary>
    public async Task<int> RunOnceAsync(DateOnly today, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Guid> due;
        using (var scope = scopes.CreateScope())
        {
            due = await scope.ServiceProvider.GetRequiredService<EmploymentEndService>().DueIdsAsync(today, cancellationToken);
        }

        var ended = 0;
        foreach (var employeeId in due)
        {
            try
            {
                if (await EndWithOneRetryAsync(employeeId, today, cancellationToken))
                {
                    ended++;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Ending the employment of employee {EmployeeId} failed; tried again on the next run.", employeeId);
            }
        }

        await RemoveStrayAssignmentsAsync(cancellationToken);
        return ended;
    }

    /// <summary>After the ends, so an employee who became Left today is covered too; a failure is logged and tried again on the next run.</summary>
    private async Task RemoveStrayAssignmentsAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var removed = await scope.ServiceProvider.GetRequiredService<EmploymentEndService>().RemoveStrayAssignmentsAsync(cancellationToken);
            if (removed > 0)
            {
                logger.LogInformation("Removed {Count} work assignment(s) dated after the last working day of their employee.", removed);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Removing work assignments after the last working day failed; tried again on the next run.");
        }
    }

    private async Task<bool> EndWithOneRetryAsync(Guid employeeId, DateOnly today, CancellationToken cancellationToken)
    {
        try
        {
            return await EndInOwnScopeAsync(employeeId, today, cancellationToken);
        }
        catch (ConcurrencyConflictException)
        {
            return await EndInOwnScopeAsync(employeeId, today, cancellationToken);
        }
    }

    private async Task<bool> EndInOwnScopeAsync(Guid employeeId, DateOnly today, CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<EmploymentEndService>().EndAsync(employeeId, today, cancellationToken);
    }
}

/// <summary>Runs <see cref="EmploymentEndJob"/> once a day at <see cref="EmploymentEndOptions.RunAt"/> (see <see cref="DailyJobHostedService"/>).</summary>
internal sealed class EmploymentEndHostedService(
    EmploymentEndJob job,
    IOptions<EmploymentEndOptions> options,
    TimeProvider timeProvider,
    ILogger<EmploymentEndHostedService> logger) : DailyJobHostedService(options.Value, timeProvider, logger)
{
    protected override string JobName => "Employment end";

    protected override async Task RunOnceAsync(DateOnly today, CancellationToken cancellationToken)
    {
        var ended = await job.RunOnceAsync(today, cancellationToken);
        Logger.LogInformation("Employment end made {Count} employees Left.", ended);
    }
}
