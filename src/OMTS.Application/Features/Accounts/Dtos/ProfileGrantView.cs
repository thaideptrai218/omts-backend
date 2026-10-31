using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Accounts.Dtos;

public sealed record ProfileGrantView(string Role, Guid? ZoneId, string? ZoneName, bool AllZones);
