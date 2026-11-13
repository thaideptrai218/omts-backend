using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Features.Sales.Invoices;

namespace OMTS.Infrastructure.Jobs;

/// <summary>How many sales awaiting their VietQR transfer one run stopped.</summary>
public sealed record PendingSaleEndOfDayRun(int Stopped);

/// <summary>
/// The end-of-day stop of the sales awaiting their VietQR transfer (council of 03/11, PM answer 12/11/2026, OMTS-224 [3.4]). It runs for the business date <c>today</c> and stops the
/// sales created before that day began, so the day that has just ended leaves none waiting. It runs at the start of <see cref="HarvestLotAutoCloseHostedService"/>'s run, before the
/// daily close check of the harvest lots (GFR-05): the kg of a stopped sale are then leftover like any kg that was not sold, instead of being held back for a transfer that will not
/// invoice them. Each sale runs in its own scope and unit of work, so a transfer arriving at that moment (it saves the same row) loses only that sale's stop: the sale is read again and,
/// now Completed, left alone. A second run stops nothing more. Any other failure is logged and the sale is stopped at the next run.
/// </summary>
public sealed class PendingSaleEndOfDayStopJob(IServiceScopeFactory scopes, ILogger<PendingSaleEndOfDayStopJob> logger)
{
    public async Task<PendingSaleEndOfDayRun> RunOnceAsync(DateOnly today, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<string> codes;
        using (var scope = scopes.CreateScope())
        {
            codes = await scope.ServiceProvider.GetRequiredService<PendingSaleEndOfDayService>().OverdueCodesAsync(today, cancellationToken);
        }

        var stopped = 0;
        foreach (var code in codes)
        {
            try
            {
                if (await StopWithOneRetryAsync(code, today, cancellationToken))
                {
                    stopped++;
                }
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogError(exception, "End-of-day stop failed for pending sale {Code}; stopped again at the next run.", code);
            }
        }

        return new PendingSaleEndOfDayRun(stopped);
    }

    private async Task<bool> StopWithOneRetryAsync(string code, DateOnly today, CancellationToken cancellationToken)
    {
        try
        {
            return await StopInOwnScopeAsync(code, today, cancellationToken);
        }
        catch (ConcurrencyConflictException)
        {
            logger.LogInformation("Pending sale {Code} was saved during the end-of-day stop; read once more.", code);
            return await StopInOwnScopeAsync(code, today, cancellationToken);
        }
    }

    private async Task<bool> StopInOwnScopeAsync(string code, DateOnly today, CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<PendingSaleEndOfDayService>().StopAsync(code, today, cancellationToken);
    }
}
