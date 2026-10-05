using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Omts.Api.Hosting;

public sealed class ApplicationReadinessHealthCheck(IHostApplicationLifetime lifetime) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        bool ready = lifetime.ApplicationStarted.IsCancellationRequested &&
            !lifetime.ApplicationStopping.IsCancellationRequested;
        return Task.FromResult(ready ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy());
    }
}
