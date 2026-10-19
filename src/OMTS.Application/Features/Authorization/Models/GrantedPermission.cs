using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Authorization.Models;

/// <summary>Permissions of one role on one function (a RolePermission row).</summary>
public sealed record GrantedPermission(
    string FunctionCode,
    bool CanView,
    bool CanCreate,
    bool CanEdit,
    bool CanDelete,
    IReadOnlyList<string> OtherPermissions,
    DataScope DataScope)
{
    public bool Allows(PermissionAction action) => action switch
    {
        PermissionAction.View => CanView,
        PermissionAction.Create => CanCreate,
        PermissionAction.Edit => CanEdit,
        PermissionAction.Delete => CanDelete,
        _ => OtherPermissions.Contains(action.ToString(), StringComparer.OrdinalIgnoreCase),
    };
}
