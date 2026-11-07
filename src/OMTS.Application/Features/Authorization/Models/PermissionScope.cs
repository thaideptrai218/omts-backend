namespace OMTS.Application.Features.Authorization.Models;

/// <summary>How the zone of a request is matched against the caller's zone–role pairs.</summary>
public enum PermissionScope
{
    /// <summary>The request targets one zone (route/query "zoneId"); a pair in that zone or in All zones is needed.</summary>
    ZoneScoped,

    /// <summary>
    /// The records have no zone (supplier payments, debts…): they follow the role only, so any Effective pair of a role
    /// holding the function is enough, whatever its zone (BR-FARM-08).
    /// </summary>
    NoZone,

    /// <summary>A pair in any zone is enough; the handler limits results to the permitted zones.</summary>
    AnyZone,
}
