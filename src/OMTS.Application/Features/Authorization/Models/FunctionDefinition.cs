namespace OMTS.Application.Features.Authorization.Models;

/// <summary>
/// One grantable function of the catalog (BR-19). <see cref="CooperativeWide"/> functions are not tied to a
/// zone, so only grants assigned at All zones apply; <see cref="SupportedActions"/> are the only
/// permissions a role may hold on it (View is always among them).
/// </summary>
public sealed record FunctionDefinition(
    string Code,
    string Name,
    bool CooperativeWide,
    IReadOnlyList<PermissionAction> SupportedActions,
    bool IsDashboardTile = false)
{
    public bool Supports(PermissionAction action) => SupportedActions.Contains(action);
}
