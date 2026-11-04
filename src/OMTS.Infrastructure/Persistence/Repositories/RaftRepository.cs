using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
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

    public Task<Raft?> FindByCodeAsync(Guid zoneId, string code, Guid? exceptRaftId, CancellationToken cancellationToken)
    {
        var upper = code.ToUpperInvariant();
        return db.Rafts.AsNoTracking().FirstOrDefaultAsync(r => r.ZoneId == zoneId && r.Code.ToUpper() == upper && r.Id != exceptRaftId, cancellationToken);
    }

    public async Task<IReadOnlyList<string>> CodesOfZoneAsync(Guid zoneId, CancellationToken cancellationToken) =>
        await db.Rafts.AsNoTracking().Where(r => r.ZoneId == zoneId).Select(r => r.Code).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Raft>> ListByZoneAsync(Guid zoneId, int? statusId, CancellationToken cancellationToken) =>
        await db.Rafts.AsNoTracking()
            .Include(r => r.Lanes)
            .Where(r => r.ZoneId == zoneId && (statusId == null || r.StatusId == statusId))
            .OrderBy(r => r.Code)
            .ToListAsync(cancellationToken);

    public Task<CultivationCycle?> GetOpenCycleAsync(Guid raftId, CancellationToken cancellationToken) =>
        db.CultivationCycles.AsNoTracking()
            .FirstOrDefaultAsync(c => c.RaftId == raftId && c.StatusId == StatusIds.CultivationCycle.Open, cancellationToken);

    public void Add(Raft raft) => db.Rafts.Add(raft);

    public void AddLane(Lane lane) => db.Lanes.Add(lane);

    public void AddPhoto(RaftPhoto photo) => db.RaftPhotos.Add(photo);

    public void RemovePhoto(RaftPhoto photo) => db.RaftPhotos.Remove(photo);
}
