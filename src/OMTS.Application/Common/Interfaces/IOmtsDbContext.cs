using Microsoft.EntityFrameworkCore;
using OMTS.Domain.Entities;

namespace OMTS.Application.Common.Interfaces;

/// <summary>
/// Persistence port used by use cases. Implemented by the EF Core context in Infrastructure;
/// add DbSets here as use cases need them.
/// </summary>
public interface IOmtsDbContext
{
    DbSet<Account> Accounts { get; }
    DbSet<AccessGrant> AccessGrants { get; }
    DbSet<Role> Roles { get; }
    DbSet<RolePermission> RolePermissions { get; }
    DbSet<LoginSession> LoginSessions { get; }
    DbSet<FarmingZone> FarmingZones { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
