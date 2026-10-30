using OMTS.Application.Features.Authorization.Models;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.AccessManagement.Dtos;

/// <param name="State">Effective, Expired, NotYetEffective or Revoked (Expired is derived from EffectiveTo).</param>
public sealed record GrantRow(
    Guid Id,
    Guid? ZoneId,
    string? ZoneName,
    bool AllZones,
    Guid RoleId,
    string RoleName,
    DateOnly EffectiveFrom,
    DateOnly? EffectiveTo,
    string State,
    DateTimeOffset? RevokedAt,
    string? RevokeReason);
