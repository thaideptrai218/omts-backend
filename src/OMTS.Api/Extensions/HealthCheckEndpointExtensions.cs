using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace OMTS.Api.Extensions;

public static class HealthCheckEndpointExtensions
{
    private const string DatabaseTag = "db";
    private const string DeploymentTag = "ready";

    /// <summary>
    /// <c>/health</c>: plain liveness, runs no check. <c>/health/db</c>: database reachable and migrated, for
    /// platform readiness and load-balancer probes. <c>/health/ready</c>: the post-deployment verification,
    /// 200 when the deployment is complete (database, migrations, status catalog, default roles, cooperative,
    /// active Owner). It depends on data the Owner can change (renaming a default role, locking the only
    /// Owner turns it 503), so it must not be used as a probe that takes instances out of rotation. The
    /// checked endpoints answer 503 with the names of the failing checks only; all are anonymous and never
    /// return business data.
    /// </summary>
    public static void MapOmtsHealthChecks(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false });
        endpoints.MapHealthChecks("/health/db", Checked(DatabaseTag));
        endpoints.MapHealthChecks("/health/ready", Checked(DeploymentTag));
    }

    private static HealthCheckOptions Checked(string tag) => new()
    {
        Predicate = check => check.Tags.Contains(tag),
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
    };
}
