using OMTS.Domain.Entities;

namespace OMTS.Application.Common.Persistence;

public interface IFarmingZoneRepository
{
    Task<IReadOnlyList<FarmingZone>> GetByIdsAsync(IReadOnlyCollection<Guid> zoneIds, CancellationToken cancellationToken);
}
