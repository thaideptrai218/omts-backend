using OMTS.Application.Features.Authorization.Models;

namespace OMTS.Application.Features.Authorization.Dtos;

public sealed record GrantView(Guid? ZoneId, string? ZoneCode, string? ZoneName, bool AllZones, string Role, IReadOnlyList<GrantedPermission> Permissions);
