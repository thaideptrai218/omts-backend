using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OMTS.Application.Features.Sales.Checkout;
using OMTS.Infrastructure.Payments.Checkout;

namespace OMTS.Infrastructure.Jobs;

/// <summary>
/// Sends again, every <see cref="CheckoutOptions.ReportRetrySeconds"/>, the reports of paid Mini App orders Zalo did not accept (CLAUDE.md §10: report until it
/// succeeds), so the trader's Zalo record shows the payment even when the trader closed the Mini App. Each run reads in its own scope; a failure is logged and the next
/// run tries again.
/// </summary>
internal sealed class TraderCheckoutReportService(IServiceScopeFactory scopes, IOptions<CheckoutOptions> options, ILogger<TraderCheckoutReportService> logger)
    : BackgroundService
{
    private const int PerRun = 20;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.ReportRetryEnabled || string.IsNullOrWhiteSpace(settings.Provider))
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Clamp(settings.ReportRetrySeconds, 10, 3600)));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<TraderCheckoutReporter>().RetryAsync(PerRun, stoppingToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogWarning(exception, "Sending the Zalo payment reports again failed; the next run tries again.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The host is stopping.
        }
    }
}
