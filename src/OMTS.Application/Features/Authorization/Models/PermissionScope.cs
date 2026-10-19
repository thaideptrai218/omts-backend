namespace OMTS.Application.Features.Authorization.Models;

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
