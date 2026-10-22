using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Infrastructure.Persistence.Repositories;

internal sealed class FarmingZoneRepository(OmtsDbContext db) : IFarmingZoneRepository
{
    public async Task<IReadOnlyList<FarmingZone>> GetByIdsAsync(IReadOnlyCollection<Guid> zoneIds, CancellationToken cancellationToken) =>
        await db.FarmingZones.AsNoTracking()
            .Where(z => zoneIds.Contains(z.Id))
            .ToListAsync(cancellationToken);

    public Task<FarmingZone?> GetForEditAsync(Guid zoneId, CancellationToken cancellationToken) =>
        db.FarmingZones.FirstOrDefaultAsync(z => z.Id == zoneId, cancellationToken);

    public Task<FarmingZone?> FindByCodeAsync(string code, Guid? exceptZoneId, CancellationToken cancellationToken)
    {
        var upper = code.ToUpperInvariant();
        return db.FarmingZones.AsNoTracking().FirstOrDefaultAsync(z => z.Code.ToUpper() == upper && z.Id != exceptZoneId, cancellationToken);
    }

    public async Task<(IReadOnlyList<ZoneWithRaftCount> Items, int Total)> SearchAsync(
        string? search, int? statusId, PermittedZones permitted, int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = Permitted(db.FarmingZones.AsNoTracking(), permitted);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var text = search.Trim();
            query = query.Where(z => z.Code.Contains(text) || z.Name.Contains(text));
        }

        if (statusId is not null)
        {
            query = query.Where(z => z.StatusId == statusId);
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderBy(z => z.Code)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(z => new ZoneWithRaftCount(z, z.Rafts.Count(r => r.StatusId == StatusIds.Usage.InUse)))
            .ToListAsync(cancellationToken);
        return (items, total);
    }

    public async Task<IReadOnlyList<FarmingZone>> ListForMapAsync(bool includeDiscontinued, PermittedZones permitted, CancellationToken cancellationToken) =>
        await Permitted(db.FarmingZones.AsNoTracking(), permitted)
            .Where(z => includeDiscontinued || z.StatusId == StatusIds.Usage.InUse)
            .Include(z => z.Rafts.Where(r => r.StatusId == StatusIds.Usage.InUse))
            .OrderBy(z => z.Code)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<FarmingZone>> ListAsync(bool includeDiscontinued, PermittedZones permitted, CancellationToken cancellationToken) =>
        await Permitted(db.FarmingZones.AsNoTracking(), permitted)
            .Where(z => includeDiscontinued || z.StatusId == StatusIds.Usage.InUse)
            .OrderBy(z => z.Code)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<string>> InUseRaftCodesAsync(Guid zoneId, CancellationToken cancellationToken) =>
        await db.Rafts.Where(r => r.ZoneId == zoneId && r.StatusId == StatusIds.Usage.InUse)
            .OrderBy(r => r.Code).Select(r => r.Code)
            .ToListAsync(cancellationToken);

    public void Add(FarmingZone zone) => db.FarmingZones.Add(zone);

    private static IQueryable<FarmingZone> Permitted(IQueryable<FarmingZone> zones, PermittedZones permitted) =>
        permitted.AllZones ? zones : zones.Where(z => permitted.ZoneIds.Contains(z.Id));
}
