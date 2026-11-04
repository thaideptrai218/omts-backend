using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Entities.Cultivation;

namespace OMTS.Infrastructure.Persistence.Repositories;

internal sealed class CultivationLogRepository(OmtsDbContext db) : ICultivationLogRepository
{
    public Task<CultivationLog?> GetForEditAsync(Guid logId, CancellationToken cancellationToken) =>
        WithCycle(db.CultivationLogs).FirstOrDefaultAsync(l => l.Id == logId, cancellationToken);

    public Task<CultivationLog?> GetAsync(Guid logId, CancellationToken cancellationToken) =>
        WithCycle(db.CultivationLogs.AsNoTracking()).FirstOrDefaultAsync(l => l.Id == logId, cancellationToken);

    public async Task<IReadOnlyList<CultivationLog>> ListByCycleAsync(Guid cycleId, CancellationToken cancellationToken) =>
        await db.CultivationLogs.AsNoTracking().Include(l => l.Photos)
            .Where(l => l.CycleId == cycleId)
            .OrderByDescending(l => l.VisitDate).ThenByDescending(l => l.CreatedAt)
            .ToListAsync(cancellationToken);

    public void Add(CultivationLog log) => db.CultivationLogs.Add(log);

    public void AddPhoto(CultivationLogPhoto photo) => db.CultivationLogPhotos.Add(photo);

    public void RemovePhoto(CultivationLogPhoto photo) => db.CultivationLogPhotos.Remove(photo);

    public void Remove(CultivationLog log)
    {
        db.CultivationLogPhotos.RemoveRange(log.Photos);
        db.CultivationLogs.Remove(log);
    }

    private static IQueryable<CultivationLog> WithCycle(IQueryable<CultivationLog> logs) =>
        logs.Include(l => l.Photos).Include(l => l.Cycle).ThenInclude(c => c.Raft).ThenInclude(r => r.Zone);
}
