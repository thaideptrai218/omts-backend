using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Entities.Cultivation;

namespace OMTS.Infrastructure.Persistence.Repositories;

internal sealed class CultivationLogRepository(OmtsDbContext db) : ICultivationLogRepository
{
    public Task<CultivationLog?> GetForEditAsync(Guid logId, CancellationToken cancellationToken) =>
        WithRaft(db.CultivationLogs).FirstOrDefaultAsync(l => l.Id == logId, cancellationToken);

    public Task<CultivationLog?> GetAsync(Guid logId, CancellationToken cancellationToken) =>
        WithRaft(db.CultivationLogs.AsNoTracking()).FirstOrDefaultAsync(l => l.Id == logId, cancellationToken);

    public async Task<IReadOnlyList<CultivationLog>> ListByCycleAsync(Guid cycleId, string? logType, CancellationToken cancellationToken) =>
        await db.CultivationLogs.AsNoTracking().Include(l => l.Photos)
            .Where(l => l.CycleId == cycleId && (logType == null || l.LogType == logType))
            .OrderByDescending(l => l.VisitDate).ThenByDescending(l => l.CreatedAt)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<CultivationLog>> ListByRaftAsync(Guid raftId, string? logType, CancellationToken cancellationToken) =>
        await db.CultivationLogs.AsNoTracking().Include(l => l.Photos).Include(l => l.Cycle)
            .Where(l => l.RaftId == raftId && (logType == null || l.LogType == logType))
            .OrderByDescending(l => l.VisitDate).ThenByDescending(l => l.CreatedAt)
            .ToListAsync(cancellationToken);

    public async Task<(IReadOnlyList<CultivationLog> Rows, int Total)> SearchAsync(
        CultivationLogFilter filter, int page, int pageSize, CancellationToken cancellationToken)
    {
        var zones = filter.ZoneIds?.ToList();
        var search = filter.Search;

        // Contains is sent as an escaped LIKE: a % or _ typed by the user matches itself.
        var query = db.CultivationLogs.AsNoTracking().Where(l =>
            (filter.ZoneId == null || l.Raft.ZoneId == filter.ZoneId)
            && (filter.RaftId == null || l.RaftId == filter.RaftId)
            && (zones == null || zones.Contains(l.Raft.ZoneId))
            && (filter.LogType == null || l.LogType == filter.LogType)
            && (filter.From == null || l.VisitDate >= filter.From)
            && (filter.To == null || l.VisitDate <= filter.To)
            && (search == null || l.Raft.Code.Contains(search) || (l.Notes != null && l.Notes.Contains(search))));
        var total = await query.CountAsync(cancellationToken);
        var rows = await Sorted(query, filter)
            .Include(l => l.Photos)
            .Include(l => l.Raft).ThenInclude(r => r.Zone)
            .Include(l => l.Cycle)
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

    // Every key is translated to SQL; the recorder's name is a correlated lookup of the account. Equal keys fall back to the recording time, then the id, so pages never repeat or skip a row.
    private IOrderedQueryable<CultivationLog> Sorted(IQueryable<CultivationLog> logs, CultivationLogFilter filter)
    {
        var desc = filter.Descending;
        var accounts = db.Accounts;
        var ordered = filter.Sort switch
        {
            CultivationLogSort.Zone => desc ? logs.OrderByDescending(l => l.Raft.Zone.Code) : logs.OrderBy(l => l.Raft.Zone.Code),
            CultivationLogSort.Raft => desc ? logs.OrderByDescending(l => l.Raft.Code) : logs.OrderBy(l => l.Raft.Code),
            CultivationLogSort.LogType => desc ? logs.OrderByDescending(l => l.LogType) : logs.OrderBy(l => l.LogType),
            CultivationLogSort.RecordedBy => desc
                ? logs.OrderByDescending(l => accounts.Where(a => a.Id == l.RecordedByAccountId).Select(a => a.DisplayName).FirstOrDefault())
                : logs.OrderBy(l => accounts.Where(a => a.Id == l.RecordedByAccountId).Select(a => a.DisplayName).FirstOrDefault()),
            CultivationLogSort.RecordedAt => desc ? logs.OrderByDescending(l => l.CreatedAt) : logs.OrderBy(l => l.CreatedAt),
            _ => desc ? logs.OrderByDescending(l => l.VisitDate) : logs.OrderBy(l => l.VisitDate),
        };
        return filter.Sort == CultivationLogSort.RecordedAt
            ? ordered.ThenByDescending(l => l.Id)
            : ordered.ThenByDescending(l => l.CreatedAt).ThenByDescending(l => l.Id);
    }

    private static IQueryable<CultivationLog> WithRaft(IQueryable<CultivationLog> logs) =>
        logs.Include(l => l.Photos).Include(l => l.Raft).ThenInclude(r => r.Zone).Include(l => l.Cycle);
}
