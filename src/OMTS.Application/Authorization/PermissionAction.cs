namespace OMTS.Application.Authorization;

/// <summary>
/// Actions a role can be granted on a function (UC12). View/Create/Edit/Delete are the fixed columns;
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
}

/// <summary>How the zone of a request is matched against the caller's zone–role grants.</summary>
public enum PermissionScope
{
    /// <summary>The request targets one zone (route/query "zoneId"); that zone's or an All zones grant is needed.</summary>
    ZoneScoped,

    /// <summary>Cooperative-wide data: only grants assigned at All zones count (section 4.4).</summary>
    AllZones,

    /// <summary>A grant in any zone is enough; the handler limits results to the permitted zones.</summary>
    AnyZone,
}
