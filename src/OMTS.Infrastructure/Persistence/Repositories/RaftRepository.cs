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
            .Include(r => r.LayoutVersions)
            .FirstOrDefaultAsync(r => r.Id == raftId, cancellationToken);

    public Task<Raft?> FindByCodeAsync(Guid zoneId, string code, Guid? exceptRaftId, CancellationToken cancellationToken)
    {
        var upper = code.ToUpperInvariant();
        return db.Rafts.AsNoTracking().FirstOrDefaultAsync(r => r.ZoneId == zoneId && r.Code.ToUpper() == upper && r.Id != exceptRaftId, cancellationToken);
    }

    public async Task<IReadOnlyList<Raft>> ListByZoneAsync(Guid zoneId, int? statusId, CancellationToken cancellationToken) =>
        await db.Rafts.AsNoTracking()
            .Include(r => r.Lanes)
            .Where(r => r.ZoneId == zoneId && (statusId == null || r.StatusId == statusId))
            .OrderBy(r => r.Code)
            .ToListAsync(cancellationToken);

    public Task<CultivationCycle?> GetOpenCycleAsync(Guid raftId, CancellationToken cancellationToken) =>
        db.CultivationCycles.AsNoTracking()
            .FirstOrDefaultAsync(c => c.RaftId == raftId && c.StatusId == StatusIds.CultivationCycle.Open, cancellationToken);

    public async Task<CultivationCycle?> FindCycleAsync(Guid raftId, Guid? cycleId, DateOnly date, CancellationToken cancellationToken)
    {
        if (cycleId is not null)
        {
            return await db.CultivationCycles.AsNoTracking().FirstOrDefaultAsync(c => c.Id == cycleId && c.RaftId == raftId, cancellationToken);
        }

        // Open on that date: opened by its end and not closed before it starts (business dates, UTC+7).
        var dayEnd = new DateTimeOffset(date.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(7));
        var dayStart = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(7));
        return await db.CultivationCycles.AsNoTracking()
            .Where(c => c.RaftId == raftId && c.OpenedAt < dayEnd && (c.ClosedAt == null || c.ClosedAt >= dayStart))
            .OrderByDescending(c => c.OpenedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<int>> LanesWithSegmentsAsync(IReadOnlyCollection<Guid> laneIds, CancellationToken cancellationToken) =>
        await db.LaneSegments.Where(s => laneIds.Contains(s.LaneId)).Select(s => s.Lane.LaneNumber).Distinct().OrderBy(n => n).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<LaneSegmentDetail>> SegmentsAsync(Guid cycleId, DateOnly date, CancellationToken cancellationToken)
    {
        var segments = await db.LaneSegments.AsNoTracking()
            .Where(s => s.CycleId == cycleId && s.SegmentDate <= date)
            .Select(s => new
            {
                s.Id, s.LaneId, s.SegmentType, s.FromPosition, s.ToPosition, s.SegmentDate, s.RecordedByAccountId, s.ConfirmedByAccountId,
                s.SeedingId, SeedLotCode = s.Seeding != null ? s.Seeding.SeedLot.InternalCode : null, s.HarvestEventId, s.LotId, s.WriteOffReason,
            })
            .ToListAsync(cancellationToken);

        var accountIds = segments.SelectMany(s => new[] { (Guid?)s.RecordedByAccountId, s.ConfirmedByAccountId }).OfType<Guid>().Distinct().ToList();
        var names = await db.Accounts.AsNoTracking().Where(a => accountIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.DisplayName, cancellationToken);

        return segments
            .Select(s => new LaneSegmentDetail(
                s.Id, s.LaneId, s.SegmentType, s.FromPosition, s.ToPosition, s.SegmentDate,
                names.GetValueOrDefault(s.RecordedByAccountId), s.ConfirmedByAccountId is { } c ? names.GetValueOrDefault(c) : null,
                s.SeedingId, s.SeedLotCode, s.HarvestEventId, s.LotId, s.WriteOffReason))
            .ToList();
    }

    public void Add(Raft raft) => db.Rafts.Add(raft);

    public void AddLane(Lane lane) => db.Lanes.Add(lane);

    public void RemoveLane(Lane lane) => db.Lanes.Remove(lane);

    public void AddLayoutVersion(RaftLayoutVersion version) => db.RaftLayoutVersions.Add(version);
}
