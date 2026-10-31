using OMTS.Application.Features.Farming.Licenses;

namespace OMTS.Application.Features.Farming.Zones.Dtos;

/// <param name="LicenseState">Worst license state today (Expired, ExpiringSoon, Missing); null when all is fine or for staff.</param>
public sealed record FarmingZoneListItem(
    Guid Id,
    string Code,
    string Name,
    string? Location,
    decimal? Area,
    int StatusId,
    string Status,
    int InUseRaftCount,
    LicenseState? LicenseState);
