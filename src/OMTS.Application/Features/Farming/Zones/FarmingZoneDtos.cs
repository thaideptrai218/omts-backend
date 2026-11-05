using OMTS.Application.Features.Farming.Licenses;

namespace OMTS.Application.Features.Farming.Zones;

/// <summary>FARM-02 create/edit body; <see cref="WaterMeasurementPoint"/> is optional; <see cref="RowVersion"/> is required on edit only.</summary>
public sealed record SaveFarmingZoneRequest(
    string? Code,
    string? Name,
    string? Location,
    decimal? Latitude,
    decimal? Longitude,
    decimal? Area,
    string? RowVersion = null);

/// <summary>Discontinue / resume body; the reason is optional and goes to the audit log.</summary>
public sealed record ChangeStatusRequest(string? Reason, string? RowVersion);

/// <summary>The proposed code of a new zone (GBR-03).</summary>
/// <param name="Code">e.g. KV03: the highest KVnn number in use (discontinued zones included) plus one.</param>
public sealed record FarmingZoneCodeProposal(string Code);

/// <param name="LicenseState">
/// Expired or ExpiringSoon: the state today of the zone's newest license that is not Replaced; null when it is fine, when
/// the zone has no license, or for staff.
/// </param>
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
    int StatusId,
    string Status,
    int InUseRaftCount,
    LicenseState? LicenseState,
    string RowVersion);

/// <summary>Answer of a zone create or edit (FARM-02.0 step 6), in the shape of the FARM-01 saves.</summary>
/// <param name="Message">MSG-GEN-05 "Farming zone saved.".</param>
/// <param name="MessageCode">The code of <paramref name="Message"/>.</param>
public sealed record FarmingZoneSaved(FarmingZoneView Zone, string Message, string MessageCode);

/// <summary>Picker row for other modules (water, harvest, sales…).</summary>
public sealed record ZoneLookupItem(Guid Id, string Code, string Name, int StatusId, string Status);

public sealed record MapRaft(Guid Id, string Code, decimal? Latitude, decimal? Longitude);

public sealed record MapZone(Guid Id, string Code, string Name, decimal? Latitude, decimal? Longitude, int StatusId, string Status, IReadOnlyList<MapRaft> Rafts);
