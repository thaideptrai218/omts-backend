using OMTS.Application.Features.Farming.Licenses;

namespace OMTS.Application.Features.Farming.Zones.Dtos;

public sealed record FarmingZoneView(
    Guid Id,
    string Code,
    string Name,
    string? Location,
    decimal? Latitude,
    decimal? Longitude,
    decimal? Area,
    string? WaterMeasurementPoint,
    int StatusId,
    string Status,
    int InUseRaftCount,
    LicenseState? LicenseState,
    string RowVersion);
