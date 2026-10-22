namespace OMTS.Application.Features.Authorization.Models;

/// <summary>
/// Actions a role can be granted on a function (ACS-10). View/Create/Edit/Delete are the fixed columns;
/// the others are stored by name in <c>RolePermission.OtherPermissions</c>.
/// </summary>
public enum PermissionAction
{
    View,
    Create,
    Edit,
    Delete,
    Export,
    Import,
    Approve,
    Publish,
}
