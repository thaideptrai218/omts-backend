using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.AccessManagement.Dtos;
using OMTS.Application.Features.Authorization;
using OMTS.Domain.Entities.Identity;

namespace OMTS.Application.Features.AccessManagement;

/// <summary>
/// The fixed roles, read only (BR-FARM-07, AC-02): roles are not created, edited, copied or discontinued in OMTS. Each
/// row shows the permissions of the §I.4.4 matrix (<see cref="FixedRoles"/>).
/// </summary>
public sealed class RoleService(IRoleRepository roles)
{
    public async Task<IReadOnlyList<RoleView>> ListAsync(CancellationToken cancellationToken) =>
        (await roles.ListAsync(cancellationToken)).Select(View).ToList();

    public async Task<Result<RoleView>> GetAsync(Guid id, CancellationToken cancellationToken) =>
        await roles.GetByIdAsync(id, cancellationToken) is { } role ? View(role) : AppError.NotFound;

    private static RoleView View(Role role) => new(
        role.Id,
        role.Code,
        role.Name,
        role.Description,
        (FixedRoles.FindByCode(role.Code)?.Permissions ?? [])
            .Select(p => new RolePermissionView(p.FunctionCode, p.Actions.Select(a => a.ToString()).ToList()))
            .OrderBy(p => p.FunctionCode)
            .ToList());
}
