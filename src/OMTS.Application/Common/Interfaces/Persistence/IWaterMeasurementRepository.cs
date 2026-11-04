using OMTS.Application.Features.Authorization.Models;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>
/// Water measurements (WATER-01…WATER-03) and the water data of zones (thresholds WATER-04, overdue marker).
/// Time ranges are [from, toExclusive). Measurements come with their raft.
/// </summary>
public interface IWaterMeasurementRepository
{
    /// <summary>The zone, tracked when it will be changed (thresholds, overdue marker).</summary>
    Task<FarmingZone?> GetZoneAsync(Guid zoneId, bool track, CancellationToken cancellationToken);

    /// <summary>Zones limited to the permitted ones, optionally In Use only, ordered by code.</summary>
    Task<IReadOnlyList<FarmingZone>> ListZonesAsync(PermittedZones permitted, bool inUseOnly, bool track, CancellationToken cancellationToken);

    /// <summary>The raft a new measurement names, tracked so the measurement picks it up.</summary>
    Task<Raft?> GetRaftAsync(Guid raftId, CancellationToken cancellationToken);

    /// <summary>Whether the zone has a raft In Use to measure at (WATER-01 PRE-2).</summary>
    Task<bool> HasRaftInUseAsync(Guid zoneId, CancellationToken cancellationToken);

    /// <summary>Whether a measurement already uses this meter photo (BR-WATER-02).</summary>
    Task<bool> IsPhotoUsedAsync(string photoKey, CancellationToken cancellationToken);

    /// <summary>The measurement with its zone, tracked.</summary>
    Task<WaterMeasurement?> GetAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>The record an account saved for a client operation, with its zone (not tracked).</summary>
    Task<WaterMeasurement?> FindByClientOperationAsync(Guid accountId, Guid clientOperationId, CancellationToken cancellationToken);

    /// <summary>Measurements of a zone in the given statuses, newest measurement first, one page.</summary>
    Task<(IReadOnlyList<WaterMeasurement> Items, int Total)> SearchAsync(
        Guid zoneId, DateTimeOffset from, DateTimeOffset toExclusive, IReadOnlyCollection<int> statusIds, int page, int pageSize,
        CancellationToken cancellationToken);

    /// <summary>All measurements of a zone in the given statuses, oldest measurement first (charts, exports).</summary>
    Task<IReadOnlyList<WaterMeasurement>> ListAsync(
        Guid zoneId, DateTimeOffset from, DateTimeOffset toExclusive, IReadOnlyCollection<int> statusIds, CancellationToken cancellationToken);

    /// <summary>
    /// The latest Active measurement of each zone that has one (not tracked, without its raft); Voided does not count.
    /// Measurements taken at the same time are ordered by the time they were saved, then id, so the latest one is always
    /// the same.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, WaterMeasurement>> LatestActiveByZoneAsync(CancellationToken cancellationToken);

    /// <summary>The zone's <paramref name="count"/> latest Active measurements (not tracked), latest first, in the order of <see cref="LatestActiveByZoneAsync"/>.</summary>
    Task<IReadOnlyList<WaterMeasurement>> ListLatestActiveAsync(Guid zoneId, int count, CancellationToken cancellationToken);

    /// <summary>Display names of the given accounts that exist.</summary>
    Task<IReadOnlyDictionary<Guid, string>> GetAccountNamesAsync(IReadOnlyCollection<Guid> accountIds, CancellationToken cancellationToken);

    void Add(WaterMeasurement measurement);
}
