using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Farming.Rafts;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Infrastructure.Persistence.Repositories;

internal sealed class RaftRepository(OmtsDbContext db) : IRaftRepository
{
    public Task<Raft?> GetForEditAsync(Guid raftId, CancellationToken cancellationToken) =>
        db.Rafts
            .Include(r => r.Zone)
            .Include(r => r.Lanes)
            .Include(r => r.Photos)
            .FirstOrDefaultAsync(r => r.Id == raftId, cancellationToken);

    public Task<Raft?> GetForReadAsync(Guid raftId, CancellationToken cancellationToken) =>
        db.Rafts.AsNoTracking()
            .Include(r => r.Zone)
            .Include(r => r.Lanes)
            .FirstOrDefaultAsync(r => r.Id == raftId, cancellationToken);

    public async Task<IReadOnlyList<Raft>> GetByIdsAsync(IReadOnlyCollection<Guid> raftIds, CancellationToken cancellationToken) =>
        raftIds.Count == 0 ? [] : await db.Rafts.AsNoTracking().Include(r => r.Zone).Where(r => raftIds.Contains(r.Id)).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Raft>> GetWithLanesByIdsAsync(IReadOnlyCollection<Guid> raftIds, CancellationToken cancellationToken) =>
        raftIds.Count == 0 ? [] : await db.Rafts.AsNoTracking().Include(r => r.Lanes).Where(r => raftIds.Contains(r.Id)).ToListAsync(cancellationToken);

    public Task<Raft?> FindByCodeAsync(Guid zoneId, string code, Guid? exceptRaftId, CancellationToken cancellationToken)
    {
        return db.Rafts.AsNoTracking().FirstOrDefaultAsync(r => r.ZoneId == zoneId && r.Code == code && r.Id != exceptRaftId, cancellationToken);
    }

    public async Task<IReadOnlyList<string>> CodesOfZoneAsync(Guid zoneId, CancellationToken cancellationToken) =>
        await db.Rafts.AsNoTracking().Where(r => r.ZoneId == zoneId).Select(r => r.Code).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Raft>> ListByZoneAsync(Guid zoneId, int? statusId, CancellationToken cancellationToken) =>
        await db.Rafts.AsNoTracking()
            .Include(r => r.Lanes)
            .Where(r => r.ZoneId == zoneId && (statusId == null || r.StatusId == statusId))
            .OrderBy(r => r.Code)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<RaftListRow>> ListRowsByZoneAsync(Guid zoneId, int? statusId, CancellationToken cancellationToken) =>
        await Rows(db.Rafts.AsNoTracking().Where(r => r.ZoneId == zoneId && (statusId == null || r.StatusId == statusId)).OrderBy(r => r.Code))
            .ToListAsync(cancellationToken);

    public async Task<(IReadOnlyList<RaftListRow> Items, int Total)> SearchRowsAsync(
        PermittedZones permitted, Guid? zoneId, int? statusId, string? search, int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = db.Rafts.AsNoTracking();
        if (!permitted.AllZones)
        {
            query = query.Where(r => permitted.ZoneIds.Contains(r.ZoneId));
        }

        if (zoneId is { } zone)
        {
            query = query.Where(r => r.ZoneId == zone);
        }

        if (statusId is { } status)
        {
            query = query.Where(r => r.StatusId == status);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var text = search.Trim();
            query = query.Where(r => r.Code.Contains(text));
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await Rows(query.OrderByDescending(r => r.Code).ThenBy(r => r.Id).Skip((page - 1) * pageSize).Take(pageSize)).ToListAsync(cancellationToken);
        return (items, total);
    }

    /// <summary>
    /// The lane counts and the Open cultivation cycle of each raft as correlated subqueries of the one statement (no query per raft).
    /// A raft has at most one Open cycle: the filtered unique index UX_CultivationCycles_RaftId_Open serves the lookup.
    /// </summary>
    private IQueryable<RaftListRow> Rows(IQueryable<Raft> rafts) => rafts.Select(r => new RaftListRow(
        r,
        r.Lanes.Count(l => l.StatusId == StatusIds.Lane.InUse),
        r.Lanes.Count(l => l.StatusId == StatusIds.Lane.InUse && l.IsDamaged),
        r.Lanes.Count(l => l.StatusId == StatusIds.Lane.InUse && l.OutOfServiceOn != null),
        db.CultivationCycles
            .Where(c => c.RaftId == r.Id && c.StatusId == StatusIds.CultivationCycle.Open)
            .Select(c => new RaftCurrentCycle(c.Id, c.StartDate))
            .FirstOrDefault()));

    public async Task<IReadOnlyList<RaftLookupItem>> ListForLookupAsync(PermittedZones permitted, CancellationToken cancellationToken) =>
        await (permitted.AllZones ? db.Rafts.AsNoTracking() : db.Rafts.AsNoTracking().Where(r => permitted.ZoneIds.Contains(r.ZoneId)))
            .OrderBy(r => r.Zone.Code).ThenBy(r => r.Code)
            .Select(r => new RaftLookupItem(r.Id, r.Code, r.Zone.Code, r.ZoneId))
            .ToListAsync(cancellationToken);

    public void Add(Raft raft) => db.Rafts.Add(raft);

    public void AddLane(Lane lane) => db.Lanes.Add(lane);

    public void AddPhoto(RaftPhoto photo) => db.RaftPhotos.Add(photo);

    public void RemovePhoto(RaftPhoto photo) => db.RaftPhotos.Remove(photo);
}
