using OMTS.Application.Features.Authorization.Models;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Application.Common.Models.Persistence;

/// <summary>A zone with the measurement time of its latest Confirmed water measurement (overdue reminder).</summary>
/// <param name="LatestConfirmedAt">When the latest measurement (by measured time) was confirmed.</param>
/// <param name="LatestId">Id of that measurement.</param>
/// <param name="LatestIsCorrection">Whether that measurement is a correction version (ENV-02).</param>
public sealed record ZoneLatestWaterMeasurement(
    FarmingZone Zone,
    DateTimeOffset? LatestConfirmedMeasuredAt,
    DateTimeOffset? LatestConfirmedAt = null,
    Guid? LatestId = null,
    bool LatestIsCorrection = false);
