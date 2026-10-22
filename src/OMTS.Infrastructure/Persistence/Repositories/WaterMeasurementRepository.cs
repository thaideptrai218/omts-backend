using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Infrastructure.Persistence.Repositories;

internal sealed class WaterMeasurementRepository(OmtsDbContext db) : IWaterMeasurementRepository
{
    // A correction chain is a handful of versions; the bound only stops a corrupted (cyclic) chain.
    private const int MaxChainLength = 1000;

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

    public Task<WaterMeasurement?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        db.WaterMeasurements.Include(m => m.Zone).FirstOrDefaultAsync(m => m.Id == id, cancellationToken);

    public Task<WaterMeasurement?> FindByClientOperationAsync(Guid accountId, Guid clientOperationId, CancellationToken cancellationToken) =>
        db.WaterMeasurements.AsNoTracking()
            .Include(m => m.Zone)
            .FirstOrDefaultAsync(m => m.MeasuredByAccountId == accountId && m.ClientOperationId == clientOperationId, cancellationToken);

    public async Task<IReadOnlyList<WaterMeasurement>> GetVersionChainAsync(Guid measurementId, CancellationToken cancellationToken)
    {
        var start = await db.WaterMeasurements.AsNoTracking().FirstOrDefaultAsync(m => m.Id == measurementId, cancellationToken);
        if (start is null)
        {
            return [];
        }

        var chain = new List<WaterMeasurement> { start };
        for (var previous = start; previous.CorrectedFromMeasurementId is { } previousId && chain.Count < MaxChainLength;)
        {
            previous = await db.WaterMeasurements.AsNoTracking().FirstAsync(m => m.Id == previousId, cancellationToken);
            chain.Insert(0, previous);
        }

        for (var last = start; chain.Count < MaxChainLength;)
        {
            var lastId = last.Id;
            var next = await db.WaterMeasurements.AsNoTracking().FirstOrDefaultAsync(m => m.CorrectedFromMeasurementId == lastId, cancellationToken);
            if (next is null)
            {
                break;
            }

            chain.Add(next);
            last = next;
        }

        return chain;
    }

    public async Task<(IReadOnlyList<WaterMeasurement> Items, int Total)> SearchAsync(
        Guid zoneId, DateTimeOffset from, DateTimeOffset toExclusive, IReadOnlyCollection<int> statusIds, int page, int pageSize,
        CancellationToken cancellationToken)
    {
        var query = InRange(zoneId, from, toExclusive, statusIds);
        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(m => m.MeasuredAt)
            .ThenByDescending(m => m.RecordVersion)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        return (items, total);
    }

    public async Task<IReadOnlyList<WaterMeasurement>> ListAsync(
        Guid zoneId, DateTimeOffset from, DateTimeOffset toExclusive, IReadOnlyCollection<int> statusIds, CancellationToken cancellationToken) =>
        await InRange(zoneId, from, toExclusive, statusIds)
            .OrderBy(m => m.MeasuredAt)
            .ThenBy(m => m.RecordVersion)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<ZoneLatestWaterMeasurement>> ListInUseZonesWithLatestConfirmedAsync(CancellationToken cancellationToken)
    {
        var zones = await db.FarmingZones.Where(z => z.StatusId == StatusIds.Usage.InUse).OrderBy(z => z.Code).ToListAsync(cancellationToken);
        var latest = await db.WaterMeasurements.AsNoTracking()
            .Where(m => m.StatusId == StatusIds.WaterMeasurement.Confirmed)
            .GroupBy(m => m.ZoneId)
            .Select(g => new { ZoneId = g.Key, MeasuredAt = g.Max(m => m.MeasuredAt) })
            .ToDictionaryAsync(x => x.ZoneId, x => x.MeasuredAt, cancellationToken);

        return zones
            .Select(z => new ZoneLatestWaterMeasurement(z, latest.TryGetValue(z.Id, out var measuredAt) ? measuredAt : null))
            .ToList();
    }

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
            .Where(m => m.ZoneId == zoneId && m.MeasuredAt >= from && m.MeasuredAt < toExclusive && ids.Contains(m.StatusId));
    }
}
