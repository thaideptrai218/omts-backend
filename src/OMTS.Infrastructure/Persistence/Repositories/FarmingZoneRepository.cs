using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Persistence;
using OMTS.Domain.Entities;

namespace OMTS.Infrastructure.Persistence.Repositories;

internal sealed class FarmingZoneRepository(OmtsDbContext db) : IFarmingZoneRepository
{
    public async Task<IReadOnlyList<FarmingZone>> GetByIdsAsync(IReadOnlyCollection<Guid> zoneIds, CancellationToken cancellationToken) =>
        await db.FarmingZones.AsNoTracking()
            .Where(z => zoneIds.Contains(z.Id))
            .ToListAsync(cancellationToken);
}
