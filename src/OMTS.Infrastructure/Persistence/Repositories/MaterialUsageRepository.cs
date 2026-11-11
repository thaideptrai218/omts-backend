using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Entities.Cultivation;

namespace OMTS.Infrastructure.Persistence.Repositories;

internal sealed class MaterialUsageRepository(OmtsDbContext db) : IMaterialUsageRepository
{
    public Task<MaterialUsage?> GetForEditAsync(Guid usageId, CancellationToken cancellationToken) =>
        Whole(db.MaterialUsages).FirstOrDefaultAsync(u => u.Id == usageId, cancellationToken);

    public Task<MaterialUsage?> GetAsync(Guid usageId, CancellationToken cancellationToken) =>
        Whole(db.MaterialUsages.AsNoTracking()).FirstOrDefaultAsync(u => u.Id == usageId, cancellationToken);

    public async Task<IReadOnlyList<MaterialUsage>> ListByRaftAsync(Guid raftId, CancellationToken cancellationToken) =>
        await db.MaterialUsages.AsNoTracking().Include(u => u.Item).Include(u => u.Issue)
            .Where(u => u.RaftId == raftId)
            .OrderByDescending(u => u.UsageDate).ThenBy(u => u.RepairReason).ThenBy(u => u.Item.Code).ThenBy(u => u.CreatedAt)
            .ToListAsync(cancellationToken);

    public void Add(MaterialUsage usage) => db.MaterialUsages.Add(usage);

    private static IQueryable<MaterialUsage> Whole(IQueryable<MaterialUsage> usages) =>
        usages.Include(u => u.Item).Include(u => u.Issue).Include(u => u.Raft).ThenInclude(r => r.Zone);
}
