using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Application.Features.WaterQuality.Dtos;

/// <summary>Minimum and maximum of each water indicator; an empty bound is not checked (BR-WATER-04).</summary>
public sealed record WaterThresholds(
    decimal? PhMin, decimal? PhMax, decimal? SalinityMin, decimal? SalinityMax, decimal? TemperatureMin = null, decimal? TemperatureMax = null)
{
    /// <summary>The zone's current thresholds (WATER-04).</summary>
    public static WaterThresholds Of(FarmingZone zone) =>
        new(zone.PhMin, zone.PhMax, zone.SalinityMin, zone.SalinityMax, zone.TemperatureMin, zone.TemperatureMax);

    /// <summary>The thresholds stored with a measurement (BR-WATER-04); corrections are evaluated against them.</summary>
    public static WaterThresholds UsedBy(WaterMeasurement measurement) => new(
        measurement.PhMinUsed, measurement.PhMaxUsed, measurement.SalinityMinUsed, measurement.SalinityMaxUsed,
        measurement.TemperatureMinUsed, measurement.TemperatureMaxUsed);

    /// <summary>The copy saved with a new measurement.</summary>
    public void CopyTo(WaterMeasurement measurement)
    {
        (measurement.PhMinUsed, measurement.PhMaxUsed) = (PhMin, PhMax);
        (measurement.SalinityMinUsed, measurement.SalinityMaxUsed) = (SalinityMin, SalinityMax);
        (measurement.TemperatureMinUsed, measurement.TemperatureMaxUsed) = (TemperatureMin, TemperatureMax);
    }
}

/// <summary>WATER-04 – set the thresholds of a zone (or of every In Use zone); each pair is set or cleared together, an omitted pair is cleared.</summary>
public sealed record SetWaterThresholdsRequest(
    decimal? PhMin, decimal? PhMax, decimal? SalinityMin, decimal? SalinityMax, decimal? TemperatureMin = null, decimal? TemperatureMax = null);

public sealed record ZoneWaterThresholds(Guid ZoneId, string ZoneCode, string ZoneName, string ZoneStatus, WaterThresholds Thresholds);
