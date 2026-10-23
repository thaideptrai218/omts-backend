using OMTS.Domain.Entities.Identity;

namespace OMTS.Application.Common.Models;

/// <summary>An effective zone–role pair with its role's permissions; <see cref="ZoneId"/> null means All zones.</summary>
public sealed record EffectiveGrant(Guid? ZoneId, Guid RoleId, string RoleName, IReadOnlyList<RolePermission> Permissions);
