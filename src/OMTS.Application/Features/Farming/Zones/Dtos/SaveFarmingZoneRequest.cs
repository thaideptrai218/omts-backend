using OMTS.Application.Features.Farming.Licenses;

namespace OMTS.Application.Features.Farming.Zones.Dtos;

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
