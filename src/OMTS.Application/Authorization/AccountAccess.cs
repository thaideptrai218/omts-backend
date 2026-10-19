using OMTS.Domain.Enums;

namespace OMTS.Application.Authorization;

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

/// <summary>An effective zone–role pair; <see cref="ZoneId"/> null means All zones.</summary>
public sealed record ZoneGrant(Guid? ZoneId, Guid RoleId, string RoleName, IReadOnlyList<GrantedPermission> Permissions);

/// <summary>Zones in which an action is allowed: everywhere, or only the listed zones.</summary>
public sealed record PermittedZones(bool AllZones, IReadOnlySet<Guid> ZoneIds)
{
    public static readonly PermittedZones None = new(false, new HashSet<Guid>());
    public static readonly PermittedZones Everywhere = new(true, new HashSet<Guid>());

    public bool IsEmpty => !AllZones && ZoneIds.Count == 0;
}

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

    /// <summary>Traders and staff need a verified phone number before other functions (BR-34, MSG67).</summary>
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

    private IEnumerable<ZoneGrant> GrantsFor(string function, PermissionAction action) =>
        Grants.Where(g => g.Permissions.Any(p => p.FunctionCode == function && p.Allows(action)));
}
