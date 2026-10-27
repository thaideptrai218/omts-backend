using OMTS.Application.Features.Authorization.Models;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Application.Common.Interfaces.Persistence;

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

/// <summary>
/// Water measurements (ENV-01…ENV-03) and the water data of zones (thresholds ENV-04, overdue marker).
/// Time ranges are [from, toExclusive).
/// </summary>
public interface IWaterMeasurementRepository
{
    /// <summary>The zone, tracked when it will be changed (thresholds, overdue marker).</summary>
    Task<FarmingZone?> GetZoneAsync(Guid zoneId, bool track, CancellationToken cancellationToken);

    /// <summary>Zones limited to the permitted ones, optionally In Use only, ordered by code.</summary>
    Task<IReadOnlyList<FarmingZone>> ListZonesAsync(PermittedZones permitted, bool inUseOnly, bool track, CancellationToken cancellationToken);

    /// <summary>The measurement with its zone, tracked.</summary>
    Task<WaterMeasurement?> GetAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>The record an account saved for a device operation, with its zone (not tracked).</summary>
    Task<WaterMeasurement?> FindByClientOperationAsync(Guid accountId, Guid clientOperationId, CancellationToken cancellationToken);

    /// <summary>Every version of the correction chain the measurement belongs to, oldest first, as stored (not tracked).</summary>
    Task<IReadOnlyList<WaterMeasurement>> GetVersionChainAsync(Guid measurementId, CancellationToken cancellationToken);

    /// <summary>Measurements of a zone in the given statuses, newest measurement first, one page.</summary>
    Task<(IReadOnlyList<WaterMeasurement> Items, int Total)> SearchAsync(
        Guid zoneId, DateTimeOffset from, DateTimeOffset toExclusive, IReadOnlyCollection<int> statusIds, int page, int pageSize,
        CancellationToken cancellationToken);

    /// <summary>All measurements of a zone in the given statuses, oldest measurement first (charts, exports).</summary>
    Task<IReadOnlyList<WaterMeasurement>> ListAsync(
        Guid zoneId, DateTimeOffset from, DateTimeOffset toExclusive, IReadOnlyCollection<int> statusIds, CancellationToken cancellationToken);

    /// <summary>
    /// Every In Use zone (tracked) with its latest Confirmed measurement time; Draft, Corrected and Voided do not count.
    /// Measurements taken at the same time are ordered by confirmation, then id, so the latest one is always the same.
    /// </summary>
    Task<IReadOnlyList<ZoneLatestWaterMeasurement>> ListInUseZonesWithLatestConfirmedAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The zone's <paramref name="count"/> latest Confirmed measurements (not tracked), latest first, in the order of
    /// <see cref="ListInUseZonesWithLatestConfirmedAsync"/>: the first is the one its overdue period starts at.
    /// </summary>
    Task<IReadOnlyList<WaterMeasurement>> ListLatestConfirmedAsync(Guid zoneId, int count, CancellationToken cancellationToken);

    /// <summary>Display names of the given accounts that exist.</summary>
    Task<IReadOnlyDictionary<Guid, string>> GetAccountNamesAsync(IReadOnlyCollection<Guid> accountIds, CancellationToken cancellationToken);

    void Add(WaterMeasurement measurement);
}
