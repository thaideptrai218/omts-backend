using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Entities.Cultivation;

namespace OMTS.Infrastructure.Persistence.Repositories;

internal sealed class CultivationCycleRepository(OmtsDbContext db) : ICultivationCycleRepository
{
    public Task<CultivationCycle?> GetForEditAsync(Guid cycleId, CancellationToken cancellationToken) =>
        db.CultivationCycles.Include(c => c.Raft).ThenInclude(r => r.Zone).FirstOrDefaultAsync(c => c.Id == cycleId, cancellationToken);

    public Task<CultivationCycle?> GetAsync(Guid cycleId, CancellationToken cancellationToken) =>
        db.CultivationCycles.AsNoTracking().Include(c => c.Raft).ThenInclude(r => r.Zone).FirstOrDefaultAsync(c => c.Id == cycleId, cancellationToken);

    public async Task<IReadOnlyList<CultivationCycle>> ListByRaftAsync(Guid raftId, CancellationToken cancellationToken) =>
        await db.CultivationCycles.AsNoTracking().Where(c => c.RaftId == raftId).OrderByDescending(c => c.StartDate).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<CultivationCycle>> ListByZoneAsync(Guid zoneId, CancellationToken cancellationToken) =>
        await db.CultivationCycles.AsNoTracking().Where(c => c.Raft.ZoneId == zoneId).ToListAsync(cancellationToken);

    public Task<DateOnly?> LastCloseDateAsync(Guid raftId, CancellationToken cancellationToken) =>
        db.CultivationCycles.AsNoTracking().Where(c => c.RaftId == raftId).MaxAsync(c => c.CloseDate, cancellationToken);

    public async Task<DateOnly?> FirstRecordDateAsync(Guid cycleId, CancellationToken cancellationToken)
    {
        var seeded = await db.SeedingRecords.AsNoTracking().Where(r => r.CycleId == cycleId).MinAsync(r => (DateOnly?)r.SeedingDate, cancellationToken);
        var visited = await db.CultivationLogs.AsNoTracking().Where(l => l.CycleId == cycleId).MinAsync(l => (DateOnly?)l.VisitDate, cancellationToken);
        return seeded is null || visited < seeded ? visited : seeded;
    }

    // A work assignment still points to its cycle (restricted key) until the labor rework derives the cycle from the date.
    public async Task<IReadOnlySet<Guid>> WithRecordsAsync(IReadOnlyCollection<Guid> cycleIds, CancellationToken cancellationToken)
    {
        if (cycleIds.Count == 0)
        {
            return new HashSet<Guid>();
        }

        var seeded = await db.SeedingRecords.AsNoTracking().Where(r => cycleIds.Contains(r.CycleId)).Select(r => r.CycleId).Distinct().ToListAsync(cancellationToken);
        var logged = await db.CultivationLogs.AsNoTracking().Where(l => cycleIds.Contains(l.CycleId)).Select(l => l.CycleId).Distinct().ToListAsync(cancellationToken);
        var assigned = await db.WorkAssignments.AsNoTracking().Where(w => w.CycleId != null && cycleIds.Contains(w.CycleId.Value)).Select(w => w.CycleId!.Value).Distinct().ToListAsync(cancellationToken);
        return seeded.Concat(logged).Concat(assigned).ToHashSet();
    }

    public async Task<int> CountRecordsAsync(Guid cycleId, CancellationToken cancellationToken) =>
        await db.SeedingRecords.CountAsync(r => r.CycleId == cycleId, cancellationToken)
        + await db.CultivationLogs.CountAsync(l => l.CycleId == cycleId, cancellationToken)
        + await db.WorkAssignments.CountAsync(w => w.CycleId == cycleId, cancellationToken);

    public void Add(CultivationCycle cycle) => db.CultivationCycles.Add(cycle);

    public void Remove(CultivationCycle cycle) => db.CultivationCycles.Remove(cycle);
}
