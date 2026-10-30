using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OMTS.Application.Features.Authorization;
using OMTS.Domain.Constants;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Persistence.Seed;
using OMTS.Infrastructure.Persistence;

namespace OMTS.Infrastructure.HealthChecks;

/// <summary>What one readiness check verifies after deployment (OMTS-200 "Deployment bootstrap check").</summary>
public enum ReadinessCheckKind
{
    Database,
    Migrations,
    StatusCatalog,
    DefaultRoles,
    Cooperative,
    CooperativeOwner,
}
