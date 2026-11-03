using OMTS.Application.Features.Authorization.Dtos;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Authorization;

/// <summary>
/// What an account may do, built from its effective zone–role grants (UC11 postconditions):
/// the union of the roles' permissions, each limited to its zone; an All zones grant applies
/// everywhere; zone A never grants zone B (NFR-01).
/// </summary>
public sealed class AccountAccess(
    Guid accountId,
    SubjectType subjectType,
    bool mustChangePassword,
    bool phoneVerified,
    IReadOnlyList<ZoneGrant> grants)
{
    public Guid AccountId { get; } = accountId;
    public SubjectType SubjectType { get; } = subjectType;
    public IReadOnlyList<ZoneGrant> Grants { get; } = grants;

    /// <summary>Full business rights in every zone (section 4.4).</summary>
    public bool IsCooperativeOwner => SubjectType == SubjectType.CooperativeOwner;

    /// <summary>Temporary password still in use: only UC04 is open (UC02 branch 2c).</summary>
    public bool RequiresPasswordChange { get; } = mustChangePassword;

    /// <summary>Traders and staff need a verified phone number before other functions (BR-34, MSG-ACCOUNT-04).</summary>
    public bool RequiresPhoneVerification { get; } =
        !phoneVerified && subjectType is SubjectType.Trader or SubjectType.Staff;

    public bool Allows(string function, PermissionAction action, PermissionScope scope, Guid? zoneId)
    {
        if (IsCooperativeOwner)
        {
            return true;
        }

        // System Administrators have no business rights; traders only reach their own data through
        // dedicated endpoints, never through the role permission matrix.
        if (SubjectType != SubjectType.Staff)
        {
            return false;
        }

        if (scope == PermissionScope.AllZones || PermissionFunctions.IsCooperativeWide(function))
        {
            return GrantsFor(function, action).Any(g => g.ZoneId is null);
        }

        return scope switch
        {
            PermissionScope.ZoneScoped => zoneId is not null && GrantsFor(function, action).Any(g => g.ZoneId is null || g.ZoneId == zoneId),
            _ => GrantsFor(function, action).Any(),
        };
    }

    /// <summary>Zones a list endpoint may return records from for this function and action.</summary>
    public PermittedZones PermittedZonesFor(string function, PermissionAction action)
    {
        if (IsCooperativeOwner)
        {
            return PermittedZones.Everywhere;
        }

        if (SubjectType != SubjectType.Staff)
        {
            return PermittedZones.None;
        }

        var grants = GrantsFor(function, action).ToList();
        if (grants.Any(g => g.ZoneId is null))
        {
            return PermittedZones.Everywhere;
        }

        return PermissionFunctions.IsCooperativeWide(function)
            ? PermittedZones.None
            : new PermittedZones(false, grants.Select(g => g.ZoneId!.Value).ToHashSet());
    }

    /// <summary>
    /// Every catalog function the account can use, with its supported actions that are allowed and where
    /// (menus of /api/me/permissions, dashboard tiles, the grant preview of FARM-05).
    /// </summary>
    public IReadOnlyList<FunctionAccess> DescribeFunctions()
    {
        var functions = new List<FunctionAccess>();
        foreach (var function in PermissionFunctions.All)
        {
            var actions = function.SupportedActions
                .Select(action => (action, zones: PermittedZonesFor(function.Code, action)))
                .Where(x => !x.zones.IsEmpty)
                .ToList();
            if (actions.Count == 0)
            {
                continue;
            }

            var allZones = actions.Any(x => x.zones.AllZones);
            var zoneIds = allZones ? new List<Guid>() : actions.SelectMany(x => x.zones.ZoneIds).Distinct().ToList();
            functions.Add(new FunctionAccess(function.Code, function.Name, allZones, zoneIds, actions.Select(x => x.action.ToString()).ToList()));
        }

        return functions;
    }

    private IEnumerable<ZoneGrant> GrantsFor(string function, PermissionAction action) =>
        Grants.Where(g => g.Permissions.Any(p => p.FunctionCode == function && p.Allows(action)));
}
