namespace OMTS.Application.Features.Authorization.Models;

/// <summary>An effective zone–role pair; <see cref="ZoneId"/> null means All zones.</summary>
public sealed record ZoneGrant(Guid? ZoneId, Guid RoleId, string RoleName, IReadOnlyList<GrantedPermission> Permissions);
