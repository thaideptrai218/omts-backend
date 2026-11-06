using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Infrastructure.Persistence.Repositories;

internal sealed class WaterMeasurementRepository(OmtsDbContext db) : IWaterMeasurementRepository
{
    public Task<FarmingZone?> GetZoneAsync(Guid zoneId, bool track, CancellationToken cancellationToken) =>
        Zones(track).FirstOrDefaultAsync(z => z.Id == zoneId, cancellationToken);

    public async Task<IReadOnlyList<FarmingZone>> ListZonesAsync(PermittedZones permitted, bool inUseOnly, bool track, CancellationToken cancellationToken)
    {
        var zoneIds = permitted.ZoneIds.ToArray();
        return await Zones(track)
            .Where(z => permitted.AllZones || zoneIds.Contains(z.Id))
            .Where(z => !inUseOnly || z.StatusId == StatusIds.Usage.InUse)
            .OrderBy(z => z.Code)
            .ToListAsync(cancellationToken);
    }

    public Task<Raft?> GetRaftAsync(Guid raftId, CancellationToken cancellationToken) =>
        db.Rafts.FirstOrDefaultAsync(r => r.Id == raftId, cancellationToken);

    public Task<bool> HasRaftInUseAsync(Guid zoneId, CancellationToken cancellationToken) =>
        db.Rafts.AnyAsync(r => r.ZoneId == zoneId && r.StatusId == StatusIds.Usage.InUse, cancellationToken);

    public Task<bool> IsPhotoUsedAsync(string photoKey, CancellationToken cancellationToken) =>
        db.WaterMeasurements.AnyAsync(m => m.PhotoUrl == photoKey, cancellationToken);

    public Task<WaterMeasurement?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        db.WaterMeasurements.Include(m => m.Zone).Include(m => m.Raft).FirstOrDefaultAsync(m => m.Id == id, cancellationToken);

    public Task<WaterMeasurement?> FindByClientOperationAsync(Guid accountId, Guid clientOperationId, CancellationToken cancellationToken) =>
        db.WaterMeasurements.AsNoTracking()
            .Include(m => m.Zone)
            .Include(m => m.Raft)
            .FirstOrDefaultAsync(m => m.MeasuredByAccountId == accountId && m.ClientOperationId == clientOperationId, cancellationToken);

    public async Task<(IReadOnlyList<WaterMeasurement> Items, int Total)> SearchAsync(
        Guid zoneId, DateTimeOffset from, DateTimeOffset toExclusive, IReadOnlyCollection<int> statusIds, int page, int pageSize,
        CancellationToken cancellationToken)
    {
        var query = InRange(zoneId, from, toExclusive, statusIds);
        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(m => m.MeasuredAt)
            .ThenByDescending(m => m.CreatedAt)
            .ThenByDescending(m => m.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        return (items, total);
    }

    public async Task<IReadOnlyList<WaterMeasurement>> ListAsync(
        Guid zoneId, DateTimeOffset from, DateTimeOffset toExclusive, IReadOnlyCollection<int> statusIds, CancellationToken cancellationToken) =>
        await InRange(zoneId, from, toExclusive, statusIds)
            .OrderBy(m => m.MeasuredAt)
            .ThenBy(m => m.CreatedAt)
            .ThenBy(m => m.Id)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, WaterMeasurement>> LatestActiveByZoneAsync(CancellationToken cancellationToken) =>
        await db.WaterMeasurements.AsNoTracking()
            .Where(m => m.StatusId == StatusIds.WaterMeasurement.Active)
            .GroupBy(m => m.ZoneId)
            .Select(g => g.OrderByDescending(m => m.MeasuredAt).ThenByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id).First())
            .ToDictionaryAsync(m => m.ZoneId, cancellationToken);

    public async Task<IReadOnlyList<WaterMeasurement>> ListLatestActiveAsync(Guid zoneId, int count, CancellationToken cancellationToken) =>
        await db.WaterMeasurements.AsNoTracking()
            .Where(m => m.ZoneId == zoneId && m.StatusId == StatusIds.WaterMeasurement.Active)
            .OrderByDescending(m => m.MeasuredAt).ThenByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id)
            .Take(count)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, string>> GetAccountNamesAsync(IReadOnlyCollection<Guid> accountIds, CancellationToken cancellationToken)
    {
        var ids = accountIds.ToArray();
        return await db.Accounts.AsNoTracking()
            .Where(a => ids.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, a => a.DisplayName, cancellationToken);
    }

    public void Add(WaterMeasurement measurement) => db.WaterMeasurements.Add(measurement);

    private IQueryable<FarmingZone> Zones(bool track) => track ? db.FarmingZones : db.FarmingZones.AsNoTracking();

    private IQueryable<WaterMeasurement> InRange(Guid zoneId, DateTimeOffset from, DateTimeOffset toExclusive, IReadOnlyCollection<int> statusIds)
    {
        var ids = statusIds.ToArray();
        return db.WaterMeasurements.AsNoTracking()
            .Include(m => m.Raft)
            .Where(m => m.ZoneId == zoneId && m.MeasuredAt >= from && m.MeasuredAt < toExclusive && ids.Contains(m.StatusId));
    }
}
