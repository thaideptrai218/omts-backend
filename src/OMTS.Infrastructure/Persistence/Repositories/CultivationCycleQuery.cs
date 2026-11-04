using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Cultivation.Progress;
using OMTS.Domain.Constants;

namespace OMTS.Infrastructure.Persistence.Repositories;

internal sealed class CultivationCycleQuery(OmtsDbContext db) : ICultivationCycleQuery
{
    public Task<CycleSpan?> OpenCycleAsync(Guid raftId, CancellationToken cancellationToken) =>
        db.CultivationCycles.AsNoTracking()
            .Where(c => c.RaftId == raftId && c.StatusId == StatusIds.CultivationCycle.Open)
            .Select(c => new CycleSpan(c.Id, c.RaftId, c.StartDate, c.CloseDate))
            .FirstOrDefaultAsync(cancellationToken);

    public Task<CycleSpan?> CycleOnDateAsync(Guid raftId, DateOnly date, CancellationToken cancellationToken) =>
        db.CultivationCycles.AsNoTracking()
            .Where(c => c.RaftId == raftId && c.StartDate <= date && (c.CloseDate == null || c.CloseDate >= date))
            .OrderByDescending(c => c.StartDate)
            .Select(c => new CycleSpan(c.Id, c.RaftId, c.StartDate, c.CloseDate))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, DateOnly>> EarliestOpenStartByZoneAsync(CancellationToken cancellationToken) =>
        await db.CultivationCycles.AsNoTracking()
            .Where(c => c.StatusId == StatusIds.CultivationCycle.Open)
            .GroupBy(c => c.Raft.ZoneId)
            .Select(g => new { ZoneId = g.Key, Earliest = g.Min(c => c.StartDate) })
            .ToDictionaryAsync(x => x.ZoneId, x => x.Earliest, cancellationToken);
}
