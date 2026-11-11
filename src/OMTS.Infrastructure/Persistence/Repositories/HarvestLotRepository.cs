using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Cultivation.Progress;
using OMTS.Application.Features.Harvest;
using OMTS.Application.Features.Purchasing;
using OMTS.Application.Features.Traceability.Publishing;
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
            .Include(l => l.ProductLines).ThenInclude(p => p.Product)
            .Where(l => l.CycleId == cycleId)
            .OrderByDescending(l => l.HarvestedAt)
            .AsSplitQuery()
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
            .Include(l => l.ProductLines).ThenInclude(p => p.Product)
            .OrderByDescending(l => l.HarvestedAt).ThenBy(l => l.LotCode)
            .AsSplitQuery()
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<HarvestLot>> ListForInvoiceAsync(IReadOnlyCollection<Guid> lotIds, CancellationToken cancellationToken)
    {
        var ids = lotIds.Distinct().ToList();
        // The product lines say whether the lot was split per product, which decides whether a sale of it writes the product ledger.
        return await db.HarvestLots.Include(l => l.Raft).ThenInclude(r => r.Zone).Include(l => l.ProductLines).Where(l => ids.Contains(l.Id)).AsSplitQuery().ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<LotLaneSeedLot>> LaneSeedLotsAsync(IReadOnlyCollection<Guid> lotIds, CancellationToken cancellationToken)
    {
        var ids = lotIds.Distinct().ToList();
        var lanes = await db.HarvestLotLanes.AsNoTracking()
            .Where(l => ids.Contains(l.LotId))
            .Select(l => new { l.LotId, l.LaneId, l.Lane.LaneNumber, l.Lot.CycleId })
            .ToListAsync(cancellationToken);
        var cycleIds = lanes.Select(l => l.CycleId).Distinct().ToList();
        var laneIds = lanes.Select(l => l.LaneId).Distinct().ToList();
        var sessions = (await db.SeedingRecordLanes.AsNoTracking()
                .Where(s => cycleIds.Contains(s.SeedingRecord.CycleId) && laneIds.Contains(s.LaneId))
                .Select(s => new
                {
                    s.SeedingRecord.CycleId,
                    s.LaneId,
                    ReceiptCode = s.SeedingRecord.SeedLotLine.Receipt.Code,
                    s.SeedingRecord.SeedLotLine.LineNumber,
                    Completed = s.SeedingRecord.SeedLotLine.Receipt.StatusId == StatusIds.PurchaseReceipt.Completed,
                })
                .ToListAsync(cancellationToken))
            .ToLookup(s => (s.CycleId, s.LaneId));
        var rows = new List<LotLaneSeedLot>();
        foreach (var lane in lanes)
        {
            var seeded = sessions[(lane.CycleId, lane.LaneId)].ToList();
            rows.AddRange(seeded.Count == 0
                ? [new LotLaneSeedLot(lane.LotId, lane.LaneNumber, null, false)]
                : seeded.Select(s => new LotLaneSeedLot(lane.LotId, lane.LaneNumber, DocumentCodes.SeedLotCode(s.ReceiptCode, s.LineNumber), s.Completed)));
        }

        return rows;
    }

    public async Task<IReadOnlyList<HarvestLot>> ListForYieldAsync(IReadOnlyCollection<Guid>? zoneIds, DateOnly from, DateOnly to, bool wholeCycles, CancellationToken cancellationToken)
    {
        var inZones = db.HarvestLots.AsNoTracking();
        if (zoneIds is not null)
        {
            var ids = zoneIds.ToList();
            inZones = inZones.Where(l => ids.Contains(l.Raft.ZoneId));
        }

        var inPeriod = inZones.Where(l => l.HarvestDate >= from && l.HarvestDate <= to);
        // A cycle belongs to one raft, so all of its lots are in the zones of the lot found in the period.
        var chosen = wholeCycles ? inZones.Where(l => inPeriod.Select(p => p.CycleId).Contains(l.CycleId)) : inPeriod;
        return await chosen.Include(l => l.Raft).ThenInclude(r => r.Zone).Include(l => l.Cycle).ToListAsync(cancellationToken);
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

    public async Task<IReadOnlyList<Guid>> ListForSaleSinceAtMostAsync(DateOnly lastDay, CancellationToken cancellationToken) =>
        await ForSale(db.HarvestLots.AsNoTracking())
            .Where(l => (l.ReopenedOn ?? l.HarvestDate) <= lastDay)
            .OrderBy(l => l.ReopenedOn ?? l.HarvestDate).ThenBy(l => l.LotCode)
            .Select(l => l.Id)
            .ToListAsync(cancellationToken);

    public void Add(HarvestLot lot) => db.HarvestLots.Add(lot);

    public void Remove(HarvestLot lot)
    {
        db.HarvestLotLanes.RemoveRange(lot.Lanes);
        db.HarvestLotPhotos.RemoveRange(lot.Photos);
        db.HarvestLotProductLines.RemoveRange(lot.ProductLines);
        db.HarvestLots.Remove(lot);
    }

    public void AddProductLine(HarvestLotProductLine line) => db.HarvestLotProductLines.Add(line);

    public void RemoveProductLine(HarvestLotProductLine line) => db.HarvestLotProductLines.Remove(line);

    public void AddLane(HarvestLotLane lane) => db.HarvestLotLanes.Add(lane);

    public void RemoveLane(HarvestLotLane lane) => db.HarvestLotLanes.Remove(lane);

    public void AddPhoto(HarvestLotPhoto photo) => db.HarvestLotPhotos.Add(photo);

    public void RemovePhoto(HarvestLotPhoto photo) => db.HarvestLotPhotos.Remove(photo);

    private static IQueryable<HarvestLot> ForSale(IQueryable<HarvestLot> lots) =>
        lots.Where(l => l.StatusId == StatusIds.HarvestLot.Open || l.StatusId == StatusIds.HarvestLot.Reopened);

    private static IQueryable<HarvestLot> WithAll(IQueryable<HarvestLot> lots) =>
        lots.Include(l => l.Lanes).ThenInclude(l => l.Lane)
            .Include(l => l.Photos)
            .Include(l => l.ProductLines).ThenInclude(p => p.Product)
            .Include(l => l.Cycle)
            .Include(l => l.Raft).ThenInclude(r => r.Zone)
            .AsSplitQuery();
}
