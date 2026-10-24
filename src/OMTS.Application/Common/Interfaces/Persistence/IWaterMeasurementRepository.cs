using OMTS.Application.Features.Authorization.Models;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>A zone with the measurement time of its latest Confirmed water measurement (overdue reminder).</summary>
/// <param name="LatestConfirmedAt">When the latest measurement (by measured time) was confirmed.</param>
public sealed record ZoneLatestWaterMeasurement(FarmingZone Zone, DateTimeOffset? LatestConfirmedMeasuredAt, DateTimeOffset? LatestConfirmedAt = null);

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

    /// <summary>Every In Use zone (tracked) with its latest Confirmed measurement time; Draft, Corrected and Voided do not count.</summary>
    Task<IReadOnlyList<ZoneLatestWaterMeasurement>> ListInUseZonesWithLatestConfirmedAsync(CancellationToken cancellationToken);

    /// <summary>Display names of the given accounts that exist.</summary>
    Task<IReadOnlyDictionary<Guid, string>> GetAccountNamesAsync(IReadOnlyCollection<Guid> accountIds, CancellationToken cancellationToken);

    void Add(WaterMeasurement measurement);
}
