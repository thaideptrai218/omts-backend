using OMTS.Application.Common.Results;
using OMTS.Application.Features.AccessManagement.Dtos;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Domain.Entities.Identity;

namespace OMTS.Application.Features.AccessManagement;

/// <summary>
/// Role permission matrix rules (ACS-10): only catalog functions, only their supported actions, and any
/// other action requires View. Rows are kept and updated in place (never deleted), so the stored matrix
/// is always the full history-free current state.
/// </summary>
public static class RoleMatrix
{
    private static readonly PermissionAction[] Fixed = [PermissionAction.View, PermissionAction.Create, PermissionAction.Edit, PermissionAction.Delete];

    /// <summary>Null when every row is valid; otherwise MSG-GEN-01 naming the function.</summary>
    public static AppError? Validate(IReadOnlyList<RolePermissionInput>? permissions)
    {
        var seen = new HashSet<string>();
        foreach (var input in permissions ?? [])
        {
            var function = PermissionFunctions.Find(input.FunctionCode);
            if (function is null || !seen.Add(function.Code))
            {
                return AppError.Invalid($"Function {input.FunctionCode}");
            }

            var actions = input.Actions ?? [];
            if (actions.Any(a => !function.Supports(a)) || (actions.Count > 0 && !actions.Contains(PermissionAction.View)))
            {
                return AppError.Invalid($"Permission of {function.Code}");
            }
        }

        return null;
    }

    /// <summary>Writes the matrix onto the role: existing rows are updated, missing ones added, omitted ones cleared.</summary>
    public static IReadOnlyList<RolePermission> Apply(Role role, IReadOnlyList<RolePermissionInput>? permissions)
    {
        var added = new List<RolePermission>();
        var byCode = (permissions ?? []).ToDictionary(p => p.FunctionCode);
        foreach (var row in role.Permissions)
        {
            Set(row, byCode.GetValueOrDefault(row.FunctionCode));
            byCode.Remove(row.FunctionCode);
        }

        foreach (var input in byCode.Values)
        {
            var row = new RolePermission { RoleId = role.Id, FunctionCode = input.FunctionCode };
            Set(row, input);
            added.Add(row);
        }

        return added;
    }

    public static IReadOnlyList<RolePermissionView> View(Role role) => role.Permissions
        .Select(p => new RolePermissionView(p.FunctionCode, ActionsOf(p).Select(a => a.ToString()).ToList(), p.DataScope))
        .Where(p => p.Actions.Count > 0)
        .OrderBy(p => p.FunctionCode)
        .ToList();

    public static IReadOnlyList<RolePermissionInput> ToInputs(Role role) => role.Permissions
        .Select(p => new RolePermissionInput(p.FunctionCode, ActionsOf(p), p.DataScope))
        .Where(p => p.Actions.Count > 0)
        .ToList();

    private static IReadOnlyList<PermissionAction> ActionsOf(RolePermission p)
    {
        var granted = AccessGrantService.ToGranted(p);
        return Enum.GetValues<PermissionAction>().Where(granted.Allows).ToList();
    }

    private static void Set(RolePermission row, RolePermissionInput? input)
    {
        var actions = input?.Actions ?? [];
        row.CanView = actions.Contains(PermissionAction.View);
        row.CanCreate = actions.Contains(PermissionAction.Create);
        row.CanEdit = actions.Contains(PermissionAction.Edit);
        row.CanDelete = actions.Contains(PermissionAction.Delete);
        row.OtherPermissions = actions.Where(a => !Fixed.Contains(a)).Select(a => a.ToString().ToUpperInvariant()).ToList();
        row.DataScope = input?.DataScope ?? row.DataScope;
    }
}
