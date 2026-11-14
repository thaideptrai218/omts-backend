using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OMTS.Application.Common;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Features.Harvest;

namespace OMTS.Infrastructure.Jobs;

/// <summary>"Jobs:HarvestLotAutoClose" section of the daily close check of harvest lots (GFR-05).</summary>
public sealed class HarvestLotAutoCloseOptions : IDailyJobOptions
{
    public const string SectionName = "Jobs:HarvestLotAutoClose";

    /// <summary>Daily run time in the business time zone (UTC+7): "at the start of the day" (PM default 00:05).</summary>
    public TimeOnly RunAt { get; set; } = new(0, 5);

    /// <summary>
    /// A host that starts after today's run time runs once at start, so that day is not skipped. Safe because a lot is closed once (it is no longer for sale) and warned once per close
    /// date (marker <c>HarvestLot.AutoCloseWarnedFor</c>).
    /// </summary>
    public bool CatchUpOnStart { get; set; } = true;
}

/// <summary>The next check is today's until <see cref="HarvestLotAutoCloseOptions.RunAt"/> business time, then tomorrow's (HARVEST-03 counts the lots it would close).</summary>
internal sealed class HarvestAutoCloseSchedule(IOptions<HarvestLotAutoCloseOptions> options, TimeProvider timeProvider) : IHarvestAutoCloseSchedule
{
    public DateOnly NextCheckDate()
    {
        var now = timeProvider.GetUtcNow();
        var today = DateOnly.FromDateTime(now.ToOffset(BusinessClock.UtcOffset).DateTime);
        return DailyJobHostedService.IsPastRunTime(now, options.Value.RunAt) ? today.AddDays(1) : today;
    }
}

/// <summary>How many lots one run of the daily close check closed and warned.</summary>
public sealed record HarvestLotAutoCloseRun(int Closed, int Warned);

/// <summary>
/// GFR-05 daily close check. Each lot runs in its own scope and unit of work, so a lot saved during the run (a sale, a close by hand, the same check on another app instance) loses only its own save:
/// it is read and checked once more at once, which then finds it closed or warned, or closes it after the sale. Any other failure is logged and the lot is checked again at the next run.
/// </summary>
public sealed class HarvestLotAutoCloseJob(IServiceScopeFactory scopes, ILogger<HarvestLotAutoCloseJob> logger)
{
    /// <summary>One check for the business date <paramref name="today"/>; running it twice on the same day closes and warns nothing more.</summary>
    public async Task<HarvestLotAutoCloseRun> RunOnceAsync(DateOnly today, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Guid> candidates;
        using (var scope = scopes.CreateScope())
        {
            candidates = await scope.ServiceProvider.GetRequiredService<HarvestLotAutoCloseService>().CandidateIdsAsync(today, cancellationToken);
        }

        var (closed, warned) = (0, 0);
        foreach (var lotId in candidates)
        {
            try
            {
                switch (await CheckWithOneRetryAsync(lotId, today, cancellationToken))
                {
                    case HarvestLotAutoCloseOutcome.Closed:
                        closed++;
                        break;
                    case HarvestLotAutoCloseOutcome.Warned:
                        warned++;
                        break;
                }
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogError(exception, "Daily close check failed for harvest lot {LotId}; checked again at the next run.", lotId);
            }
        }

        return new HarvestLotAutoCloseRun(closed, warned);
    }

    private async Task<HarvestLotAutoCloseOutcome> CheckWithOneRetryAsync(Guid lotId, DateOnly today, CancellationToken cancellationToken)
    {
        try
        {
            return await CheckInOwnScopeAsync(lotId, today, cancellationToken);
        }
        catch (ConcurrencyConflictException)
        {
            logger.LogInformation("Harvest lot {LotId} was saved during the daily close check; checked once more.", lotId);
            return await CheckInOwnScopeAsync(lotId, today, cancellationToken);
        }
    }

    private async Task<HarvestLotAutoCloseOutcome> CheckInOwnScopeAsync(Guid lotId, DateOnly today, CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<HarvestLotAutoCloseService>().CheckAsync(lotId, today, cancellationToken);
    }
}

/// <summary>
/// Runs once a day at <see cref="HarvestLotAutoCloseOptions.RunAt"/> (see <see cref="DailyJobHostedService"/>) first the end-of-day stop of the sales awaiting their VietQR transfer
/// (<see cref="PendingSaleEndOfDayStopJob"/>), then <see cref="HarvestLotAutoCloseJob"/>. One service runs both in that order, so the stop is always done before the close check,
/// also in the catch-up run of a host started late; a failed stop is logged and does not keep the lots from being checked.
/// </summary>
internal sealed class HarvestLotAutoCloseHostedService(
    PendingSaleEndOfDayStopJob pendingSaleStop,
    HarvestLotAutoCloseJob job,
    IOptions<HarvestLotAutoCloseOptions> options,
    TimeProvider timeProvider,
    ILogger<HarvestLotAutoCloseHostedService> logger) : DailyJobHostedService(options.Value, timeProvider, logger)
{
    protected override string JobName => "Harvest lot daily close check";

    protected override async Task RunOnceAsync(DateOnly today, CancellationToken cancellationToken)
    {
        try
        {
            var stop = await pendingSaleStop.RunOnceAsync(today, cancellationToken);
            Logger.LogInformation("Pending sale end-of-day stop stopped {Stopped} sales.", stop.Stopped);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            Logger.LogError(exception, "Pending sale end-of-day stop failed; the harvest lot daily close check goes on.");
        }

        var run = await job.RunOnceAsync(today, cancellationToken);
        Logger.LogInformation("Harvest lot daily close check closed {Closed} lots and warned {Warned}.", run.Closed, run.Warned);
    }
}
