namespace OMTS.Application.Features.Authorization.Models;

/// <summary>
/// An Effective zone–role pair with the permissions of its fixed role; <see cref="ZoneId"/> null means All zones.
/// <see cref="RoleCode"/> is ZONE_MANAGER or ACCOUNTANT.
/// </summary>
public sealed record ZoneGrant(Guid? ZoneId, Guid RoleId, string RoleCode, string RoleName, IReadOnlyList<GrantedPermission> Permissions);
