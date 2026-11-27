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

    public async Task<IReadOnlyList<CultivationLog>> ListByCycleAsync(Guid cycleId, string? logType, CancellationToken cancellationToken) =>
        await db.CultivationLogs.AsNoTracking().Include(l => l.Photos)
            .Where(l => l.CycleId == cycleId && (logType == null || l.LogType == logType))
            .OrderByDescending(l => l.VisitDate).ThenByDescending(l => l.CreatedAt)
            .ToListAsync(cancellationToken);

    public async Task<(IReadOnlyList<CultivationLog> Rows, int Total)> SearchAsync(
        CultivationLogFilter filter, int page, int pageSize, CancellationToken cancellationToken)
    {
        var zones = filter.ZoneIds?.ToList();
        var search = filter.Search;

        // Contains is sent as an escaped LIKE: a % or _ typed by the user matches itself.
        var query = db.CultivationLogs.AsNoTracking().Where(l =>
            (filter.ZoneId == null || l.Cycle.Raft.ZoneId == filter.ZoneId)
            && (filter.RaftId == null || l.Cycle.RaftId == filter.RaftId)
            && (zones == null || zones.Contains(l.Cycle.Raft.ZoneId))
            && (filter.LogType == null || l.LogType == filter.LogType)
            && (filter.From == null || l.VisitDate >= filter.From)
            && (filter.To == null || l.VisitDate <= filter.To)
            && (search == null || l.Cycle.Raft.Code.Contains(search) || (l.Notes != null && l.Notes.Contains(search))));
        var total = await query.CountAsync(cancellationToken);
        var rows = await query
            .OrderByDescending(l => l.VisitDate).ThenByDescending(l => l.CreatedAt).ThenByDescending(l => l.Id)
            .Include(l => l.Photos)
            .Include(l => l.Cycle).ThenInclude(c => c.Raft).ThenInclude(r => r.Zone)
            .AsSplitQuery()
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(cancellationToken);
        return (rows, total);
    }

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
