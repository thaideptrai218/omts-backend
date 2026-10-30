using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OMTS.Application.Features.Authorization;
using OMTS.Domain.Constants;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Persistence;
using OMTS.Infrastructure.Persistence.Seed;

namespace OMTS.Infrastructure.HealthChecks;

/// <summary>
/// One <c>/health/ready</c> check. Results carry no business data or configuration values; the endpoint
/// only lists the names of failing checks.
/// </summary>
internal sealed class DeploymentReadinessCheck(OmtsDbContext db, ReadinessCheckKind kind) : IHealthCheck
{
    public const string Tag = "ready";

    /// <summary>Database and migrations only: what a platform readiness or load-balancer probe needs.</summary>
    public const string DatabaseTag = "db";

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            return await IsReadyAsync(cancellationToken) ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Never echo the exception: it may contain server names or connection details.
            return HealthCheckResult.Unhealthy();
        }
    }

    private async Task<bool> IsReadyAsync(CancellationToken cancellationToken) => kind switch
    {
        ReadinessCheckKind.Database => await db.Database.CanConnectAsync(cancellationToken),
        // Non-relational test databases have no migrations.
        ReadinessCheckKind.Migrations => !db.Database.IsRelational() || !(await db.Database.GetPendingMigrationsAsync(cancellationToken)).Any(),
        ReadinessCheckKind.StatusCatalog => await HasCompleteStatusCatalogAsync(cancellationToken),
        ReadinessCheckKind.DefaultRoles => await HasDefaultRolesAsync(cancellationToken),
        ReadinessCheckKind.Cooperative => await db.Cooperatives.AnyAsync(cancellationToken),
        ReadinessCheckKind.CooperativeOwner => await db.Accounts.AnyAsync(
            a => a.SubjectType == SubjectType.CooperativeOwner && a.StatusId == StatusIds.Account.Active, cancellationToken),
        _ => false,
    };

    private async Task<bool> HasCompleteStatusCatalogAsync(CancellationToken cancellationToken)
    {
        var statusIds = (await db.Statuses.Select(s => s.Id).ToListAsync(cancellationToken)).ToHashSet();
        var transitionIds = (await db.StatusTransitions.Select(t => t.Id).ToListAsync(cancellationToken)).ToHashSet();
        return StatusCatalogSeed.Statuses.All(s => statusIds.Contains(s.Id))
            && StatusCatalogSeed.Transitions.All(t => transitionIds.Contains(t.Id));
    }

    private async Task<bool> HasDefaultRolesAsync(CancellationToken cancellationToken)
    {
        var names = DefaultRoles.All.Select(r => r.Name).ToList();
        return await db.Roles.CountAsync(r => names.Contains(r.Name), cancellationToken) == names.Count;
    }
}
