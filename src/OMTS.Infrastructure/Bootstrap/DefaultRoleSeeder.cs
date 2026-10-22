using Microsoft.EntityFrameworkCore;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Domain.Entities.Identity;
using OMTS.Infrastructure.Persistence;

namespace OMTS.Infrastructure.Bootstrap;

/// <summary>Creates the default roles that do not exist yet; never touches a role the Owner already has.</summary>
internal static class DefaultRoleSeeder
{
    public static async Task<IReadOnlyList<string>> SeedAsync(OmtsDbContext db, AuditLogService audit, CancellationToken cancellationToken)
    {
        var existing = await db.Roles.Select(r => r.Name).ToListAsync(cancellationToken);
        var created = new List<string>();
        foreach (var template in DefaultRoles.All.Where(t => !existing.Contains(t.Name, StringComparer.OrdinalIgnoreCase)))
        {
            var role = new Role { Name = template.Name, Description = template.Description };
            foreach (var permission in template.Permissions)
            {
                role.Permissions.Add(ToRolePermission(permission));
            }

            db.Roles.Add(role);
            audit.Record(new AuditEntry("Role.Created", nameof(Role))
            {
                RecordId = role.Id,
                RecordCode = role.Name,
                After = new { role.Name, Permissions = role.Permissions.Select(p => new { p.FunctionCode, p.CanView, p.CanCreate, p.CanEdit, p.CanDelete, p.OtherPermissions }) },
            });
            created.Add(role.Name);
        }

        return created;
    }

    private static RolePermission ToRolePermission(DefaultRolePermission permission)
    {
        var actions = DefaultRoles.ActionsOf(permission);
        return new RolePermission
        {
            FunctionCode = permission.FunctionCode,
            CanView = actions.Contains(PermissionAction.View),
            CanCreate = actions.Contains(PermissionAction.Create),
            CanEdit = actions.Contains(PermissionAction.Edit),
            CanDelete = actions.Contains(PermissionAction.Delete),
            OtherPermissions = actions
                .Where(a => a is not (PermissionAction.View or PermissionAction.Create or PermissionAction.Edit or PermissionAction.Delete))
                .Select(a => a.ToString().ToUpperInvariant())
                .ToList(),
        };
    }
}
