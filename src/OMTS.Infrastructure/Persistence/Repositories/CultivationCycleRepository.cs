using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Entities.Cultivation;

namespace OMTS.Infrastructure.Persistence.Repositories;

internal sealed class CultivationCycleRepository(OmtsDbContext db) : ICultivationCycleRepository
{
    public Task<CultivationCycle?> GetForEditAsync(Guid cycleId, CancellationToken cancellationToken) =>
        db.CultivationCycles.Include(c => c.Raft).ThenInclude(r => r.Zone).FirstOrDefaultAsync(c => c.Id == cycleId, cancellationToken);

    public Task<CultivationCycle?> GetAsync(Guid cycleId, CancellationToken cancellationToken) =>
        db.CultivationCycles.AsNoTracking().Include(c => c.Raft).FirstOrDefaultAsync(c => c.Id == cycleId, cancellationToken);

    public async Task<IReadOnlyList<CultivationCycle>> ListByRaftAsync(Guid raftId, CancellationToken cancellationToken) =>
        await db.CultivationCycles.AsNoTracking().Where(c => c.RaftId == raftId).OrderByDescending(c => c.StartDate).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<CultivationCycle>> ListByZoneAsync(Guid zoneId, CancellationToken cancellationToken) =>
        await db.CultivationCycles.AsNoTracking().Where(c => c.Raft.ZoneId == zoneId).ToListAsync(cancellationToken);

    public Task<DateOnly?> LastCloseDateAsync(Guid raftId, CancellationToken cancellationToken) =>
        db.CultivationCycles.AsNoTracking().Where(c => c.RaftId == raftId).MaxAsync(c => c.CloseDate, cancellationToken);

    // SeedingEvent stands for the seeding session until C2; C2 and C4 change this to SeedingRecord and CultivationLog.
    public Task<DateOnly?> FirstRecordDateAsync(Guid cycleId, CancellationToken cancellationToken) =>
        db.SeedingEvents.AsNoTracking().Where(e => e.CycleId == cycleId).MinAsync(e => (DateOnly?)e.SeedingDate, cancellationToken);

    // A work assignment still points to its cycle (restricted key) until the labor rework derives the cycle from the date.
    public async Task<IReadOnlySet<Guid>> WithRecordsAsync(IReadOnlyCollection<Guid> cycleIds, CancellationToken cancellationToken)
    {
        if (cycleIds.Count == 0)
        {
            return new HashSet<Guid>();
        }

        var seeded = await db.SeedingEvents.AsNoTracking().Where(e => cycleIds.Contains(e.CycleId)).Select(e => e.CycleId).Distinct().ToListAsync(cancellationToken);
        var assigned = await db.WorkAssignments.AsNoTracking().Where(w => w.CycleId != null && cycleIds.Contains(w.CycleId.Value)).Select(w => w.CycleId!.Value).Distinct().ToListAsync(cancellationToken);
        return seeded.Concat(assigned).ToHashSet();
    }

    public void Add(CultivationCycle cycle) => db.CultivationCycles.Add(cycle);

    public void Remove(CultivationCycle cycle) => db.CultivationCycles.Remove(cycle);
}
