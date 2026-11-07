using Microsoft.EntityFrameworkCore;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Authorization;
using OMTS.Domain.Entities.Identity;
using OMTS.Infrastructure.Persistence;

namespace OMTS.Infrastructure.Bootstrap;

/// <summary>
/// Creates the rows of the fixed roles (BR-FARM-07) that do not exist yet, found by code. Their permissions are the
/// static matrix of <see cref="FixedRoles"/>, never stored, so an existing row is left as it is.
/// </summary>
internal static class DefaultRoleSeeder
{
    public static async Task<IReadOnlyList<string>> SeedAsync(OmtsDbContext db, AuditLogService audit, CancellationToken cancellationToken)
    {
        var existing = await db.Roles.Select(r => r.Code).ToListAsync(cancellationToken);
        var created = new List<string>();
        foreach (var fixedRole in FixedRoles.All.Where(r => !existing.Contains(r.Code)))
        {
            var role = new Role { Code = fixedRole.Code, Name = fixedRole.Name, Description = fixedRole.Description };
            db.Roles.Add(role);
            audit.Record(new AuditEntry("Role.Created", nameof(Role))
            {
                RecordId = role.Id,
                RecordCode = role.Code,
                After = new { role.Code, role.Name },
            });
            created.Add(role.Name);
        }

        return created;
    }
}
