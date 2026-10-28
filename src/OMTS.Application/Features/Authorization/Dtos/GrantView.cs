using OMTS.Application.Features.Authorization.Models;

namespace OMTS.Application.Features.Authorization.Dtos;

/// <param name="Role">Display name of the fixed role.</param>
/// <param name="RoleCode">ZONE_MANAGER or ACCOUNTANT.</param>
public sealed record GrantView(
    Guid? ZoneId, string? ZoneCode, string? ZoneName, bool AllZones, string Role, string RoleCode, IReadOnlyList<GrantedPermission> Permissions);
