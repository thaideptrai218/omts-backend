using OMTS.Domain.Entities.Farming;

namespace OMTS.Application.Common.Interfaces.Persistence;

public interface IFarmingZoneRepository
{
    Task<IReadOnlyList<FarmingZone>> GetByIdsAsync(IReadOnlyCollection<Guid> zoneIds, CancellationToken cancellationToken);
}
