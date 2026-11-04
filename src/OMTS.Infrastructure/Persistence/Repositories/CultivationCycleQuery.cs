using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Constants;

namespace OMTS.Infrastructure.Persistence.Repositories;

/// <summary>
/// Reads the cycle table as it is before the CYCLE model (OMTS-220): a cycle's start date is the Vietnam date of
/// <c>OpenedAt</c>. The merge with OMTS-220 replaces this class with its version over <c>StartDate</c>.
/// </summary>
internal sealed class CultivationCycleQuery(OmtsDbContext db) : ICultivationCycleQuery
{
    public async Task<IReadOnlyDictionary<Guid, DateOnly>> EarliestOpenStartByZoneAsync(CancellationToken cancellationToken)
    {
        var earliest = await db.CultivationCycles.AsNoTracking()
            .Where(c => c.StatusId == StatusIds.CultivationCycle.Open)
            .GroupBy(c => c.Raft.ZoneId)
            .Select(g => new { ZoneId = g.Key, Earliest = g.Min(c => c.OpenedAt) })
            .ToListAsync(cancellationToken);
        return earliest.ToDictionary(x => x.ZoneId, x => BusinessClock.ToBusinessDate(x.Earliest));
    }
}
