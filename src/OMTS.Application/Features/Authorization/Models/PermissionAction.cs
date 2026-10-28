namespace OMTS.Application.Features.Authorization.Models;

/// <summary>
/// Actions a fixed role may hold on a function (BR-FARM-07). View/Create/Edit/Delete are the fixed columns of
/// <see cref="GrantedPermission"/>; the others are kept by name in <see cref="GrantedPermission.OtherPermissions"/>.
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
