using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Cultivation.Progress;
using OMTS.Domain.Entities.Cultivation;

namespace OMTS.Infrastructure.Persistence.Repositories;

internal sealed class SeedingRecordRepository(OmtsDbContext db) : ISeedingRecordRepository
{
    public Task<SeedingRecord?> GetForEditAsync(Guid recordId, CancellationToken cancellationToken) =>
        WithCycle(db.SeedingRecords).FirstOrDefaultAsync(r => r.Id == recordId, cancellationToken);

    public Task<SeedingRecord?> GetAsync(Guid recordId, CancellationToken cancellationToken) =>
        WithCycle(db.SeedingRecords.AsNoTracking()).FirstOrDefaultAsync(r => r.Id == recordId, cancellationToken);

    public async Task<IReadOnlyList<SeedingRecord>> ListByCycleAsync(Guid cycleId, CancellationToken cancellationToken) =>
        await WithLot(db.SeedingRecords.AsNoTracking())
            .Where(r => r.CycleId == cycleId)
            .OrderByDescending(r => r.SeedingDate).ThenByDescending(r => r.CreatedAt)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<LaneSeeding>> LaneSeedingsAsync(Guid cycleId, CancellationToken cancellationToken) =>
        await db.SeedingRecordLanes.AsNoTracking()
            .Where(l => l.SeedingRecord.CycleId == cycleId)
            .Select(l => new LaneSeeding(cycleId, l.LaneId, l.SeedingRecord.SeedingDate))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, CycleSeedTotal>> SeedTotalsByCycleAsync(IReadOnlyCollection<Guid> cycleIds, CancellationToken cancellationToken)
    {
        var rows = await db.SeedingRecords.AsNoTracking()
            .Where(r => cycleIds.Contains(r.CycleId))
            .Select(r => new { r.CycleId, r.Quantity, Unit = r.SeedLotLine.Item != null ? r.SeedLotLine.Item.Unit : r.SeedLotLine.Unit })
            .ToListAsync(cancellationToken);
        return rows.GroupBy(r => r.CycleId).ToDictionary(
            g => g.Key, g => new CycleSeedTotal(g.Sum(r => r.Quantity), g.Select(r => r.Unit).FirstOrDefault(u => !string.IsNullOrWhiteSpace(u))));
    }

    public void Add(SeedingRecord record) => db.SeedingRecords.Add(record);

    public void AddLane(SeedingRecordLane lane) => db.SeedingRecordLanes.Add(lane);

    public void RemoveLane(SeedingRecordLane lane) => db.SeedingRecordLanes.Remove(lane);

    public void Remove(SeedingRecord record)
    {
        db.SeedingRecordLanes.RemoveRange(record.Lanes);
        db.SeedingRecords.Remove(record);
    }

    private static IQueryable<SeedingRecord> WithLot(IQueryable<SeedingRecord> records) =>
        records.Include(r => r.Issue).Include(r => r.Lanes).ThenInclude(l => l.Lane)
            .Include(r => r.SeedLotLine).ThenInclude(l => l.Item)
            .Include(r => r.SeedLotLine).ThenInclude(l => l.Receipt).ThenInclude(r => r.Supplier);

    private static IQueryable<SeedingRecord> WithCycle(IQueryable<SeedingRecord> records) =>
        WithLot(records).Include(r => r.Cycle).ThenInclude(c => c.Raft).ThenInclude(r => r.Zone);
}
