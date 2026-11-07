using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Application.Features.Authorization;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Persistence;

namespace OMTS.Api.IntegrationTests.Infrastructure;

/// <summary>
/// The fixed roles (BR-FARM-07) in tests: the role rows the deployment seed creates, and zone–role pairs of them. Roles
/// are never built ad hoc: a test gets the real Zone Manager or Accountant rights of the §I.4.4 matrix.
/// </summary>
public static class FixedRoleTestKit
{
    // One lock for every host: creating a missing row is a quick check, and two tests on one host must not both add it.
    private static readonly SemaphoreSlim RowLock = new(1, 1);

    /// <summary>The row of the fixed role, created as the deployment seed does when the database has none yet.</summary>
    public static async Task<Role> RoleAsync(this WebApplicationFactory<Program> api, StaffRole role)
    {
        var fixedRole = FixedRoles.Of(role);
        await RowLock.WaitAsync();
        try
        {
            using var scope = api.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OmtsDbContext>();
            if (await db.Roles.AsNoTracking().FirstOrDefaultAsync(r => r.Code == fixedRole.Code) is { } existing)
            {
                return existing;
            }

            var created = new Role { Code = fixedRole.Code, Name = fixedRole.Name, Description = fixedRole.Description };
            db.Roles.Add(created);
            await db.SaveChangesAsync();
            return created;
        }
        finally
        {
            RowLock.Release();
        }
    }

    /// <summary>Both fixed role rows, for tests that call the API with a role code.</summary>
    public static async Task EnsureFixedRolesAsync(this WebApplicationFactory<Program> api)
    {
        foreach (var role in Enum.GetValues<StaffRole>())
        {
            await api.RoleAsync(role);
        }
    }

    /// <summary>An Effective pair of the fixed <paramref name="role"/> in the zone, or in All zones when null.</summary>
    /// <param name="grantedBy">The granting Owner; the account itself when null.</param>
    public static async Task<AccessGrant> GrantAsync(
        this WebApplicationFactory<Program> api, Account account, StaffRole role, Guid? zoneId, Guid? grantedBy = null, Action<AccessGrant>? configure = null)
    {
        var roleRow = await api.RoleAsync(role);
        var grant = new AccessGrant
        {
            AccountId = account.Id,
            RoleId = roleRow.Id,
            ZoneId = zoneId,
            ScopeType = zoneId is null ? AccessScopeType.AllZones : AccessScopeType.SpecificZone,
            GrantedByAccountId = grantedBy ?? account.Id,
        };
        configure?.Invoke(grant);
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OmtsDbContext>();
        db.AccessGrants.Add(grant);
        await db.SaveChangesAsync();
        return grant;
    }
}
