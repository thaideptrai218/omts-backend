using OMTS.Application.Features.Farming.Licenses;

namespace OMTS.Application.Features.Farming.Zones;

/// <summary>FARM-02 create/edit body; <see cref="RowVersion"/> is required on edit only.</summary>
public sealed record SaveFarmingZoneRequest(
    string? Code,
    string? Name,
    string? Location,
    decimal? Latitude,
    decimal? Longitude,
    decimal? Area,
    string? WaterMeasurementPoint,
    string? RowVersion = null);

/// <summary>Discontinue / reactivate body; the reason is optional and goes to the audit log.</summary>
public sealed record ChangeStatusRequest(string? Reason, string? RowVersion);

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

/// <summary>Picker row for other modules (water, harvest, sales…).</summary>
public sealed record ZoneLookupItem(Guid Id, string Code, string Name, int StatusId, string Status);

public sealed record MapRaft(Guid Id, string Code, decimal? Latitude, decimal? Longitude);

public sealed record MapZone(Guid Id, string Code, string Name, decimal? Latitude, decimal? Longitude, int StatusId, string Status, IReadOnlyList<MapRaft> Rafts);
