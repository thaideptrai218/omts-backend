using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Cultivation.Progress;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Harvest;

namespace OMTS.Infrastructure.Persistence.Repositories;

internal sealed class HarvestLotRepository(OmtsDbContext db) : IHarvestLotRepository
{
    public Task<HarvestLot?> GetForEditAsync(Guid lotId, CancellationToken cancellationToken) =>
        WithAll(db.HarvestLots).FirstOrDefaultAsync(l => l.Id == lotId, cancellationToken);

    public Task<HarvestLot?> GetAsync(Guid lotId, CancellationToken cancellationToken) =>
        WithAll(db.HarvestLots.AsNoTracking()).FirstOrDefaultAsync(l => l.Id == lotId, cancellationToken);

    public async Task<IReadOnlyList<HarvestLot>> ListByCycleAsync(Guid cycleId, CancellationToken cancellationToken) =>
        await db.HarvestLots.AsNoTracking().Include(l => l.Lanes).ThenInclude(l => l.Lane)
            .Where(l => l.CycleId == cycleId)
            .OrderByDescending(l => l.HarvestedAt)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<LaneHarvest>> LaneHarvestsAsync(Guid cycleId, CancellationToken cancellationToken) =>
        await db.HarvestLotLanes.AsNoTracking()
            .Where(l => l.Lot.CycleId == cycleId)
            .Select(l => new LaneHarvest(cycleId, l.LaneId, l.Lot.HarvestDate, l.IsFinished))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<HarvestLotLane>> ListOfLaneAsync(Guid cycleId, Guid laneId, CancellationToken cancellationToken) =>
        await db.HarvestLotLanes.AsNoTracking().Include(l => l.Lot)
            .Where(l => l.LaneId == laneId && l.Lot.CycleId == cycleId)
            .OrderByDescending(l => l.Lot.HarvestedAt)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<string>> CodesFromAsync(string baseCode, CancellationToken cancellationToken)
    {
        var withSuffix = $"{baseCode}-";
        return await db.HarvestLots.AsNoTracking()
            .Where(l => l.LotCode == baseCode || l.LotCode.StartsWith(withSuffix))
            .Select(l => l.LotCode)
            .ToListAsync(cancellationToken);
    }

    public Task<DateOnly?> LastHarvestDateAsync(Guid cycleId, CancellationToken cancellationToken) =>
        db.HarvestLots.AsNoTracking()
            .Where(l => l.CycleId == cycleId)
            .OrderByDescending(l => l.HarvestDate)
            .Select(l => (DateOnly?)l.HarvestDate)
            .FirstOrDefaultAsync(cancellationToken);

    public Task<int> CountForSaleSinceAtMostAsync(DateOnly lastDay, CancellationToken cancellationToken) =>
        db.HarvestLots.AsNoTracking()
            .Where(l => l.StatusId == StatusIds.HarvestLot.Open || l.StatusId == StatusIds.HarvestLot.Reopened)
            .CountAsync(l => (l.ReopenedOn ?? l.HarvestDate) <= lastDay, cancellationToken);

    public void Add(HarvestLot lot) => db.HarvestLots.Add(lot);

    private static IQueryable<HarvestLot> WithAll(IQueryable<HarvestLot> lots) =>
        lots.Include(l => l.Lanes).ThenInclude(l => l.Lane)
            .Include(l => l.Photos)
            .Include(l => l.Cycle)
            .Include(l => l.Raft).ThenInclude(r => r.Zone);
}
