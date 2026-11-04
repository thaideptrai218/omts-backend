using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Cultivation.Progress;
using OMTS.Application.Features.Harvest;
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

    public async Task<IReadOnlyList<LotLaneFact>> LaneFactsAsync(Guid cycleId, CancellationToken cancellationToken) =>
        await db.HarvestLotLanes.AsNoTracking()
            .Where(l => l.Lot.CycleId == cycleId)
            .Select(l => new LotLaneFact(l.LotId, l.LaneId, l.Lot.HarvestDate, l.Lot.HarvestedAt, l.IsFinished))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<string>> CodesFromAsync(string baseCode, CancellationToken cancellationToken)
    {
        var withSuffix = $"{baseCode}-";
        var inUse = await db.HarvestLots.AsNoTracking()
            .Where(l => l.LotCode == baseCode || l.LotCode.StartsWith(withSuffix))
            .Select(l => l.LotCode)
            .ToListAsync(cancellationToken);
        var everGiven = await db.AuditEvents.AsNoTracking()
            .Where(e => e.Action == HarvestLotAudit.Created && e.RecordCode != null && (e.RecordCode == baseCode || e.RecordCode.StartsWith(withSuffix)))
            .Select(e => e.RecordCode!)
            .ToListAsync(cancellationToken);
        return [.. inUse, .. everGiven];
    }

    public async Task<IReadOnlyList<HarvestLot>> ListForSaleAsync(IReadOnlyCollection<Guid>? zoneIds, CancellationToken cancellationToken)
    {
        var forSale = ForSale(db.HarvestLots.AsNoTracking());
        if (zoneIds is not null)
        {
            var ids = zoneIds.ToList();
            forSale = forSale.Where(l => ids.Contains(l.Raft.ZoneId));
        }

        return await forSale
            .Include(l => l.Raft).ThenInclude(r => r.Zone)
            .OrderByDescending(l => l.HarvestedAt).ThenBy(l => l.LotCode)
            .ToListAsync(cancellationToken);
    }

    public Task<int> CountForSaleAsync(Guid? zoneId, CancellationToken cancellationToken)
    {
        var forSale = ForSale(db.HarvestLots.AsNoTracking());
        return (zoneId is { } id ? forSale.Where(l => l.Raft.ZoneId == id) : forSale).CountAsync(cancellationToken);
    }

    public Task<DateOnly?> LastHarvestDateAsync(Guid cycleId, CancellationToken cancellationToken) =>
        db.HarvestLots.AsNoTracking()
            .Where(l => l.CycleId == cycleId)
            .OrderByDescending(l => l.HarvestDate)
            .Select(l => (DateOnly?)l.HarvestDate)
            .FirstOrDefaultAsync(cancellationToken);

    public Task<int> CountForSaleSinceAtMostAsync(DateOnly lastDay, CancellationToken cancellationToken) =>
        ForSale(db.HarvestLots.AsNoTracking()).CountAsync(l => (l.ReopenedOn ?? l.HarvestDate) <= lastDay, cancellationToken);

    public void Add(HarvestLot lot) => db.HarvestLots.Add(lot);

    public void Remove(HarvestLot lot)
    {
        db.HarvestLotLanes.RemoveRange(lot.Lanes);
        db.HarvestLotPhotos.RemoveRange(lot.Photos);
        db.HarvestLots.Remove(lot);
    }

    public void AddLane(HarvestLotLane lane) => db.HarvestLotLanes.Add(lane);

    public void RemoveLane(HarvestLotLane lane) => db.HarvestLotLanes.Remove(lane);

    public void AddPhoto(HarvestLotPhoto photo) => db.HarvestLotPhotos.Add(photo);

    public void RemovePhoto(HarvestLotPhoto photo) => db.HarvestLotPhotos.Remove(photo);

    public void ForgetTrackedChanges() => db.ChangeTracker.Clear();

    private static IQueryable<HarvestLot> ForSale(IQueryable<HarvestLot> lots) =>
        lots.Where(l => l.StatusId == StatusIds.HarvestLot.Open || l.StatusId == StatusIds.HarvestLot.Reopened);

    private static IQueryable<HarvestLot> WithAll(IQueryable<HarvestLot> lots) =>
        lots.Include(l => l.Lanes).ThenInclude(l => l.Lane)
            .Include(l => l.Photos)
            .Include(l => l.Cycle)
            .Include(l => l.Raft).ThenInclude(r => r.Zone);
}
