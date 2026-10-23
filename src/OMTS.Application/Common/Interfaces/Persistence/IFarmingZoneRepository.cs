using OMTS.Application.Features.Authorization.Models;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>A zone with the number of its In Use rafts, for the zone list (FARM-02).</summary>
public sealed record ZoneWithRaftCount(FarmingZone Zone, int InUseRaftCount);

public interface IFarmingZoneRepository
{
    Task<IReadOnlyList<FarmingZone>> GetByIdsAsync(IReadOnlyCollection<Guid> zoneIds, CancellationToken cancellationToken);

    /// <summary>Tracked for editing.</summary>
    Task<FarmingZone?> GetForEditAsync(Guid zoneId, CancellationToken cancellationToken);

    /// <summary>A zone whose code equals <paramref name="code"/> ignoring case, other than <paramref name="exceptZoneId"/>.</summary>
    Task<FarmingZone?> FindByCodeAsync(string code, Guid? exceptZoneId, CancellationToken cancellationToken);

    /// <summary>Zones by code or name (contains), optional status, limited to the permitted zones, ordered by code.</summary>
    Task<(IReadOnlyList<ZoneWithRaftCount> Items, int Total)> SearchAsync(
        string? search, int? statusId, PermittedZones permitted, int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>Zones (In Use, plus Discontinued when asked) with their In Use rafts, for the map.</summary>
    Task<IReadOnlyList<FarmingZone>> ListForMapAsync(bool includeDiscontinued, PermittedZones permitted, CancellationToken cancellationToken);

    /// <summary>Zones (In Use, plus Discontinued when asked) limited to the permitted zones, ordered by code, untracked.</summary>
    Task<IReadOnlyList<FarmingZone>> ListAsync(bool includeDiscontinued, PermittedZones permitted, CancellationToken cancellationToken);

    /// <summary>Codes of the zone's In Use rafts.</summary>
    Task<IReadOnlyList<string>> InUseRaftCodesAsync(Guid zoneId, CancellationToken cancellationToken);

    void Add(FarmingZone zone);
}
