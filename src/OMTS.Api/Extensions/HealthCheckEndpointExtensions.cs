using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace OMTS.Api.Extensions;

public static class HealthCheckEndpointExtensions
{
    /// <summary>
    /// <c>/health</c>: plain liveness, runs no check. <c>/health/ready</c>: 200 when the deployment is
    /// complete (database, migrations, status catalog, default roles, cooperative, active Owner), else 503
    /// with the names of the failing checks only. Both are anonymous and never return business data.
    /// </summary>
    public static void MapOmtsHealthChecks(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false });
        endpoints.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains("ready"),
            ResultStatusCodes =
            {
                [HealthStatus.Healthy] = StatusCodes.Status200OK,
                [HealthStatus.Degraded] = StatusCodes.Status503ServiceUnavailable,
                [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable,
            },
            ResponseWriter = (context, report) => context.Response.WriteAsJsonAsync(new
            {
                status = report.Status.ToString(),
                failing = report.Entries.Where(e => e.Value.Status != HealthStatus.Healthy).Select(e => e.Key).ToList(),
            }),
        });
    }
}
