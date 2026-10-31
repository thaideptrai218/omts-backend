using OMTS.Application.Features.Authorization.Models;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.AccessManagement.Dtos;

/// <summary>One zone–role row of a grant form; <see cref="ZoneId"/> null means All zones.</summary>
public sealed record GrantDraft(Guid? ZoneId, Guid RoleId, DateOnly? EffectiveFrom = null, DateOnly? EffectiveTo = null);
