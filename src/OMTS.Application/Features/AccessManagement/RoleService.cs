using OMTS.Application.Common;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.AccessManagement.Dtos;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;

namespace OMTS.Application.Features.AccessManagement;

/// <summary>
/// ACS-10 – the Cooperative Owner defines roles with permissions per catalog function (dashboard tiles
/// included), copies them, and discontinues instead of deleting. Edits apply at once to every holder.
/// </summary>
public sealed class RoleService(
    IRoleRepository roles,
    IAccessGrantRepository grants,
    IStatusCatalog statusCatalog,
    RowVersionGuard rowVersions,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    public const int MaxNameLength = 100;
    public const int MaxDescriptionLength = 1000;

    public async Task<IReadOnlyList<RoleView>> ListAsync(int? statusId, CancellationToken cancellationToken) =>
        (await roles.ListAsync(statusId, cancellationToken)).Select(View).ToList();

    public async Task<Result<RoleView>> GetAsync(Guid id, CancellationToken cancellationToken) =>
        await roles.GetByIdAsync(id, cancellationToken) is { } role ? View(role) : AppError.NotFound;

    public async Task<Result<RoleView>> CreateAsync(SaveRoleRequest request, CancellationToken cancellationToken)
    {
        var checkedRequest = await ValidateAsync(request, null, cancellationToken);
        if (!checkedRequest.IsSuccess)
        {
            return checkedRequest.Error!;
        }

        var (name, description) = checkedRequest.Value!;
        var role = new Role { Name = name, Description = description };
        roles.Add(role);
        foreach (var row in RoleMatrix.Apply(role, request.Permissions))
        {
            role.Permissions.Add(row);
        }

        audit.Record(new AuditEntry("Role.Created", nameof(Role))
        {
            RecordId = role.Id,
            RecordCode = role.Name,
            After = new { role.Name, role.Description, Permissions = RoleMatrix.View(role) },
        });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return View(role);
    }

    public async Task<Result<RoleView>> UpdateAsync(Guid id, SaveRoleRequest request, CancellationToken cancellationToken)
    {
        var role = await roles.GetByIdAsync(id, cancellationToken);
        if (role is null)
        {
            return AppError.NotFound;
        }

        var checkedRequest = await ValidateAsync(request, role.Id, cancellationToken);
        if (!checkedRequest.IsSuccess)
        {
            return checkedRequest.Error!;
        }

        if (await rowVersions.CheckAsync(role, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        var before = new { role.Name, role.Description, Permissions = RoleMatrix.View(role) };
        (role.Name, role.Description) = checkedRequest.Value!;
        foreach (var row in RoleMatrix.Apply(role, request.Permissions))
        {
            roles.AddPermission(row);
        }

        // Touch the role itself so its row version moves even when only permission rows changed.
        role.UpdatedAt = timeProvider.GetUtcNow();
        audit.Record(new AuditEntry("Role.Updated", nameof(Role))
        {
            RecordId = role.Id,
            RecordCode = role.Name,
            Before = before,
            After = new { role.Name, role.Description, Permissions = RoleMatrix.View(role) },
        });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return View(role);
    }

    /// <summary>A new role pre-filled from the source role's matrix, with its own unique name.</summary>
    public async Task<Result<RoleView>> CopyAsync(Guid id, CopyRoleRequest request, CancellationToken cancellationToken)
    {
        var source = await roles.GetByIdAsync(id, cancellationToken);
        if (source is null)
        {
            return AppError.NotFound;
        }

        var created = await CreateAsync(new SaveRoleRequest(request.Name, request.Description ?? source.Description, RoleMatrix.ToInputs(source)), cancellationToken);
        if (created.IsSuccess)
        {
            audit.Record(new AuditEntry("Role.Copied", nameof(Role)) { RecordId = created.Value!.Id, RecordCode = created.Value.Name, After = new { CopiedFrom = source.Name } });
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return created;
    }

    /// <summary>In Use → Discontinued only when no Effective grant uses the role (MSG11 lists the holders).</summary>
    public async Task<Result<RoleView>> DiscontinueAsync(Guid id, CancellationToken cancellationToken)
    {
        var role = await roles.GetByIdAsync(id, cancellationToken);
        if (role is null)
        {
            return AppError.NotFound;
        }

        var holders = await grants.GetEffectiveForRoleAsync(role.Id, timeProvider.Today(), cancellationToken);
        if (holders.Count > 0)
        {
            var names = holders.Select(g => g.Account.DisplayName).Distinct().ToList();
            return AppError.Conflict(UserMessages.StillInUse(holders.Count, string.Join(", ", names)));
        }

        return await ChangeStatusAsync(role, StatusIds.Usage.InUse, StatusIds.Usage.Discontinued, cancellationToken);
    }

    public async Task<Result<RoleView>> ReactivateAsync(Guid id, CancellationToken cancellationToken) =>
        await roles.GetByIdAsync(id, cancellationToken) is { } role
            ? await ChangeStatusAsync(role, StatusIds.Usage.Discontinued, StatusIds.Usage.InUse, cancellationToken)
            : AppError.NotFound;

    /// <summary>Accounts and zones an edit would affect, for the MSG09 confirmation.</summary>
    public async Task<Result<RoleImpact>> ImpactAsync(Guid id, CancellationToken cancellationToken)
    {
        if (await roles.GetByIdAsync(id, cancellationToken) is null)
        {
            return AppError.NotFound;
        }

        var holders = await grants.GetEffectiveForRoleAsync(id, timeProvider.Today(), cancellationToken);
        return new RoleImpact(
            holders.Select(g => g.AccountId).Distinct().Count(),
            holders.Where(g => g.ZoneId is not null).Select(g => g.ZoneId).Distinct().Count(),
            holders.Any(g => g.ZoneId is null),
            holders.Select(g => g.Account.DisplayName).Distinct().ToList(),
            UserMessages.ConfirmRoleEdit.Text);
    }

    private async Task<Result<RoleView>> ChangeStatusAsync(Role role, int from, int to, CancellationToken cancellationToken)
    {
        if (role.StatusId != from)
        {
            var current = statusCatalog.DisplayNameOf(role.StatusId);
            return AppError.Conflict(UserMessages.CannotChangeStatus("Role", current, statusCatalog.DisplayNameOf(to), $"the role is already {current.ToLowerInvariant()}"));
        }

        role.StatusId = to;
        audit.RecordStatusChange(nameof(Role), role.Id, role.Name, from, to);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return View(role);
    }

    private async Task<Result<(string Name, string? Description)>> ValidateAsync(SaveRoleRequest request, Guid? roleId, CancellationToken cancellationToken)
    {
        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > MaxNameLength)
        {
            return AppError.Invalid("Role name");
        }

        var description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        if (description?.Length > MaxDescriptionLength)
        {
            return AppError.Invalid("Description");
        }

        if (RoleMatrix.Validate(request.Permissions) is { } invalid)
        {
            return invalid;
        }

        if (await roles.FindByNameAsync(name, cancellationToken) is { } existing && existing.Id != roleId)
        {
            return AppError.AlreadyExists("Role", existing.Id);
        }

        return (name, description);
    }

    private RoleView View(Role role) => new(
        role.Id,
        role.Name,
        role.Description,
        statusCatalog.DisplayNameOf(role.StatusId),
        RoleMatrix.View(role),
        RowVersionGuard.Encode(role.RowVersion));
}
