using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using OMTS.Infrastructure.Identity.Revocation;

namespace OMTS.Infrastructure.HealthChecks;

/// <summary>
/// The revocation store behind <c>/health/ready</c>. Redis being down or cold only means every request is checked
/// against the database, which is slower but correct, so this check reports Degraded and never Unhealthy. It is not
/// part of the database probe or of liveness.
/// </summary>
internal sealed class SessionRevocationReadinessCheck(IOptions<SessionRevocationOptions> options, IServiceProvider services) : IHealthCheck
{
    public const string Name = "session-revocation";

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!options.Value.UseRedis)
        {
            return HealthCheckResult.Healthy();
        }

        try
        {
            var store = (RedisSessionRevocationStore)services.GetService(typeof(RedisSessionRevocationStore))!;
            return await store.IsCompleteAsync() ? HealthCheckResult.Healthy() : HealthCheckResult.Degraded();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Never echo the exception: it may contain server names or connection details.
            return HealthCheckResult.Degraded();
        }
    }
}
