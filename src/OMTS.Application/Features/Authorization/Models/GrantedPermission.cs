using static OMTS.Application.Features.Authorization.Models.PermissionAction;

namespace OMTS.Application.Features.Authorization.Models;

/// <summary>Permissions of one fixed role on one function (a row of the matrix of <see cref="FixedRoles"/>).</summary>
public sealed record GrantedPermission(
    string FunctionCode,
    bool CanView,
    bool CanCreate,
    bool CanEdit,
    bool CanDelete,
    IReadOnlyList<string> OtherPermissions)
{
    public static GrantedPermission Of(string functionCode, IReadOnlyCollection<PermissionAction> actions) => new(
        functionCode,
        actions.Contains(View),
        actions.Contains(Create),
        actions.Contains(Edit),
        actions.Contains(Delete),
        actions.Where(a => a is not (View or Create or Edit or Delete)).Select(a => a.ToString().ToUpperInvariant()).ToList());

    public bool Allows(PermissionAction action) => action switch
    {
        View => CanView,
        Create => CanCreate,
        Edit => CanEdit,
        Delete => CanDelete,
        _ => OtherPermissions.Contains(action.ToString(), StringComparer.OrdinalIgnoreCase),
    };
}
