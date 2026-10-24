using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Application.Features.WaterQuality.Dtos;

/// <summary>Minimum and maximum of each water indicator; an empty bound is not checked (pH 0–14, salinity 0–100 ‰).</summary>
public sealed record WaterThresholds(decimal? PhMin, decimal? PhMax, decimal? SalinityMin, decimal? SalinityMax)
{
    /// <summary>The zone's current thresholds (ENV-04).</summary>
    public static WaterThresholds Of(FarmingZone zone) => new(zone.PhMin, zone.PhMax, zone.SalinityMin, zone.SalinityMax);

    /// <summary>The thresholds stored with a confirmed measurement (BR-07); corrections are evaluated against them.</summary>
    public static WaterThresholds UsedBy(WaterMeasurement measurement) =>
        new(measurement.PhMinUsed, measurement.PhMaxUsed, measurement.SalinityMinUsed, measurement.SalinityMaxUsed);
}

/// <summary>ENV-04 – set the thresholds of a zone (or of every In Use zone); each pair is set or cleared together.</summary>
public sealed record SetWaterThresholdsRequest(decimal? PhMin, decimal? PhMax, decimal? SalinityMin, decimal? SalinityMax);

public sealed record ZoneWaterThresholds(Guid ZoneId, string ZoneCode, string ZoneName, string ZoneStatus, WaterThresholds Thresholds);
