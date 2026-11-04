namespace OMTS.Application.Features.Authorization.Models;

/// <summary>
/// One function of the catalog (BR-FARM-07). <see cref="CooperativeWide"/> functions work on records with no zone, so
/// they follow the role only: any Effective pair of a role holding the function is enough, whatever its zone
/// (BR-FARM-08). <see cref="SupportedActions"/> are the actions the function has; a fixed role holds some of them
/// (View is always among them), the Cooperative Owner all of them.
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
