using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Cultivation.Progress;
using OMTS.Domain.Entities.Cultivation;

namespace OMTS.Infrastructure.Persistence.Repositories;

internal sealed class LaneProgressRepository(OmtsDbContext db) : ILaneProgressRepository
{
    public async Task<IReadOnlyList<LaneProgressStep>> StepsAsync(Guid cycleId, CancellationToken cancellationToken) =>
        await db.LaneProgressChanges.AsNoTracking()
            .Where(c => c.CycleId == cycleId)
            .OrderBy(c => c.OccurredAt)
            .Select(c => new LaneProgressStep(
                c.CycleId, c.LaneId, c.OccurredAt, c.OccurredOn, c.ChangedByAccountId, c.Reason, c.OldSeededPercent, c.OldHarvestedPercent, c.SeededPercent, c.HarvestedPercent))
            .ToListAsync(cancellationToken);

    public void Add(LaneProgressChange change) => db.LaneProgressChanges.Add(change);
}
