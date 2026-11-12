using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Purchasing;

namespace OMTS.Infrastructure.Persistence.Repositories;

internal sealed class NurseryBatchRepository(OmtsDbContext db) : INurseryBatchRepository
{
    public Task<NurseryBatch?> GetForEditAsync(Guid batchId, CancellationToken cancellationToken) =>
        Detailed(db.NurseryBatches).AsSplitQuery().FirstOrDefaultAsync(b => b.Id == batchId, cancellationToken);

    public Task<NurseryBatch?> GetAsync(Guid batchId, CancellationToken cancellationToken) =>
        Detailed(db.NurseryBatches.AsNoTracking()).AsSplitQuery().FirstOrDefaultAsync(b => b.Id == batchId, cancellationToken);

    public async Task<(IReadOnlyList<NurseryBatch> Rows, int Total, NurseryListTotals Totals)> SearchAsync(
        NurseryBatchFilter filter, int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = Filtered(db.NurseryBatches.AsNoTracking(), filter);
        var total = await query.CountAsync(cancellationToken);
        var live = query.Where(b => b.StatusId != StatusIds.NurseryBatch.Cancelled);
        var totals = new NurseryListTotals(
            await live.SumAsync(b => (decimal?)b.QuantityIn, cancellationToken) ?? 0m,
            await live.Where(b => b.StatusId == StatusIds.NurseryBatch.Nursing).SumAsync(b => (decimal?)b.QuantityIn, cancellationToken) ?? 0m,
            await live.Where(b => b.StatusId == StatusIds.NurseryBatch.Completed).SumAsync(b => (decimal?)b.QuantityIn, cancellationToken) ?? 0m,
            await live.Where(b => b.StatusId == StatusIds.NurseryBatch.Completed).SumAsync(b => b.Survivors, cancellationToken) ?? 0m,
            await query.CountAsync(b => b.StatusId == StatusIds.NurseryBatch.Nursing, cancellationToken),
            await query.CountAsync(b => b.StatusId == StatusIds.NurseryBatch.Completed, cancellationToken));
        var rows = await Sorted(query, filter)
            .Include(b => b.Zone)
            .Include(b => b.SeedLotLine).ThenInclude(l => l.Item)
            .Include(b => b.SeedLotLine).ThenInclude(l => l.Receipt).ThenInclude(r => r.Supplier)
            .Include(b => b.Allocations)
            .AsSplitQuery()
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(cancellationToken);
        return (rows, total, totals);
    }

    public async Task<IReadOnlyList<NurseryBatch>> CompletedAsync(Guid? zoneId, IReadOnlySet<Guid>? zoneIds, CancellationToken cancellationToken)
    {
        var zones = zoneIds?.ToList();
        return await Detailed(db.NurseryBatches.AsNoTracking())
            .Where(b => b.StatusId == StatusIds.NurseryBatch.Completed && (zoneId == null || b.ZoneId == zoneId) && (zones == null || zones.Contains(b.ZoneId)))
            .OrderByDescending(b => b.CompletedOn).ThenByDescending(b => b.Code)
            .AsSplitQuery()
            .ToListAsync(cancellationToken);
    }

    public Task<PurchaseReceiptLine?> SeedLotOfAsync(Guid batchId, CancellationToken cancellationToken) =>
        db.PurchaseReceiptLines
            .Include(l => l.Item)
            .Include(l => l.Receipt).ThenInclude(r => r.Supplier)
            .FirstOrDefaultAsync(l => db.NurseryBatches.Any(b => b.Id == batchId && b.SeedLotLineId == l.Id), cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, NurseryCycleFacts>> FactsByCycleAsync(IReadOnlyCollection<Guid> cycleIds, CancellationToken cancellationToken)
    {
        if (cycleIds.Count == 0)
        {
            return new Dictionary<Guid, NurseryCycleFacts>();
        }

        var rows = await db.CultivationCycles.AsNoTracking()
            .Where(c => cycleIds.Contains(c.Id) && c.NurseryBatchId != null)
            .Join(db.NurseryBatches, c => c.NurseryBatchId, b => b.Id, (c, b) => new { CycleId = c.Id, c.NurseryAllocatedQuantity, b.Id, b.Code, b.QuantityIn, b.Survivors })
            .ToListAsync(cancellationToken);
        var seeded = await SeededByCycleAsync(rows.Select(r => r.CycleId).ToList(), cancellationToken);
        return rows.ToDictionary(
            r => r.CycleId,
            r => new NurseryCycleFacts(r.Id, r.Code, r.QuantityIn, r.Survivors ?? 0m, r.NurseryAllocatedQuantity ?? 0m, seeded.GetValueOrDefault(r.CycleId)));
    }

    public async Task<IReadOnlyDictionary<Guid, decimal>> SeededByCycleAsync(IReadOnlyCollection<Guid> cycleIds, CancellationToken cancellationToken) =>
        cycleIds.Count == 0
            ? new Dictionary<Guid, decimal>()
            : (await db.SeedingRecords.AsNoTracking().Where(r => cycleIds.Contains(r.CycleId)).GroupBy(r => r.CycleId)
                .Select(g => new { CycleId = g.Key, Quantity = g.Sum(r => r.Quantity) }).ToListAsync(cancellationToken))
                .ToDictionary(r => r.CycleId, r => r.Quantity);

    public void Add(NurseryBatch batch) => db.NurseryBatches.Add(batch);

    public void AddAllocation(NurseryBatchAllocation allocation) => db.NurseryBatchAllocations.Add(allocation);

    public void RemoveAllocation(NurseryBatchAllocation allocation) => db.NurseryBatchAllocations.Remove(allocation);

    public void AddPhoto(NurseryBatchPhoto photo) => db.NurseryBatchPhotos.Add(photo);

    public void RemovePhoto(NurseryBatchPhoto photo) => db.NurseryBatchPhotos.Remove(photo);

    private static IQueryable<NurseryBatch> Detailed(IQueryable<NurseryBatch> batches) => batches
        .Include(b => b.Zone)
        .Include(b => b.SeedLotLine).ThenInclude(l => l.Item)
        .Include(b => b.SeedLotLine).ThenInclude(l => l.Receipt).ThenInclude(r => r.Supplier)
        .Include(b => b.Issue)
        .Include(b => b.Allocations).ThenInclude(a => a.Raft)
        .Include(b => b.Allocations).ThenInclude(a => a.Cycle)
        .Include(b => b.Photos);

    private static IQueryable<NurseryBatch> Filtered(IQueryable<NurseryBatch> batches, NurseryBatchFilter filter)
    {
        var search = filter.Search?.Trim();
        var zones = filter.ZoneIds?.ToList();
        var statuses = filter.StatusIds?.ToList();
        return batches.Where(b =>
            (filter.ZoneId == null || b.ZoneId == filter.ZoneId)
            && (zones == null || zones.Contains(b.ZoneId))
            && (statuses == null || statuses.Contains(b.StatusId))
            && (filter.From == null || b.StartDate >= filter.From)
            && (filter.To == null || b.StartDate <= filter.To)
            && (string.IsNullOrEmpty(search)
                || b.Code.Contains(search)
                || (b.Note != null && b.Note.Contains(search))
                || b.SeedLotLine.Receipt.Code.Contains(search)
                || b.SeedLotLine.Receipt.Supplier.Name.Contains(search)
                || (b.SeedLotLine.Item != null && b.SeedLotLine.Item.Name.Contains(search))));
    }

    private static IQueryable<NurseryBatch> Sorted(IQueryable<NurseryBatch> batches, NurseryBatchFilter filter)
    {
        var desc = filter.Descending;
        IOrderedQueryable<NurseryBatch> ordered = filter.Sort switch
        {
            NurseryBatchSort.StartDate => desc ? batches.OrderByDescending(b => b.StartDate) : batches.OrderBy(b => b.StartDate),
            NurseryBatchSort.QuantityIn => desc ? batches.OrderByDescending(b => b.QuantityIn) : batches.OrderBy(b => b.QuantityIn),
            NurseryBatchSort.Status => desc ? batches.OrderByDescending(b => b.StatusId) : batches.OrderBy(b => b.StatusId),
            _ => desc ? batches.OrderByDescending(b => b.Code) : batches.OrderBy(b => b.Code),
        };
        return ordered.ThenByDescending(b => b.Code);
    }
}
