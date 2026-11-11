using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Infrastructure.Persistence.Repositories;

internal sealed class ProductStockRepository(OmtsDbContext db) : IProductStockRepository
{
    public async Task<IReadOnlyDictionary<Guid, decimal>> OnHandAsync(IReadOnlyCollection<Guid> productIds, CancellationToken cancellationToken)
    {
        if (productIds.Count == 0)
        {
            return new Dictionary<Guid, decimal>();
        }

        var ids = productIds.ToList();
        var sums = await db.ProductStockMovements.AsNoTracking().Where(m => ids.Contains(m.ProductId))
            .GroupBy(m => m.ProductId).Select(g => new { g.Key, Sum = g.Sum(m => m.QuantityKg) }).ToListAsync(cancellationToken);
        return ids.Distinct().ToDictionary(id => id, id => sums.FirstOrDefault(s => s.Key == id)?.Sum ?? 0m);
    }

    public async Task<IReadOnlyList<ProductStockMovement>> UnreversedOfDocumentAsync(Guid documentId, CancellationToken cancellationToken) =>
        await db.ProductStockMovements.AsNoTracking()
            .Where(m => m.DocumentId == documentId && m.Kind != ProductStockMovementKind.Reversal
                && !db.ProductStockMovements.Any(r => r.ReversesMovementId == m.Id))
            .OrderBy(m => m.Sequence).ToListAsync(cancellationToken);

    public async Task<IReadOnlyDictionary<(Guid ProductId, Guid LotId), decimal>> LotBalancesAsync(
        IReadOnlyCollection<Guid> productIds, IReadOnlyCollection<Guid> lotIds, CancellationToken cancellationToken)
    {
        if (productIds.Count == 0 || lotIds.Count == 0)
        {
            return new Dictionary<(Guid, Guid), decimal>();
        }

        var products = productIds.ToList();
        var lots = lotIds.ToList();
        var sums = await db.ProductStockMovements.AsNoTracking().Where(m => m.HarvestLotId != null && lots.Contains(m.HarvestLotId.Value) && products.Contains(m.ProductId))
            .GroupBy(m => new { m.ProductId, LotId = m.HarvestLotId!.Value }).Select(g => new { g.Key.ProductId, g.Key.LotId, Sum = g.Sum(m => m.QuantityKg) }).ToListAsync(cancellationToken);
        return sums.ToDictionary(s => (s.ProductId, s.LotId), s => s.Sum);
    }

    public async Task<IReadOnlyList<ProductMovementRow>> ForProductAsync(Guid productId, bool includeReversals, IReadOnlySet<Guid>? zoneIds, CancellationToken cancellationToken)
    {
        var query = db.ProductStockMovements.AsNoTracking().Where(m => m.ProductId == productId);
        if (zoneIds is not null)
        {
            var zones = zoneIds.ToList();
            query = query.Where(m => m.HarvestLotId != null && db.HarvestLots.Any(l => l.Id == m.HarvestLotId && zones.Contains(l.Raft.ZoneId)));
        }

        var rows = await query.OrderBy(m => m.OccurredAt).ThenBy(m => m.Sequence).ToListAsync(cancellationToken);
        if (!includeReversals)
        {
            // An edit or cancellation undoes rows with reversal rows: both sides are left out, so the running balance still adds up.
            var undone = rows.Where(m => m.ReversesMovementId != null).Select(m => m.ReversesMovementId!.Value).ToHashSet();
            rows = rows.Where(m => m.Kind != ProductStockMovementKind.Reversal && !undone.Contains(m.Id)).ToList();
        }

        var balance = 0m;
        return rows.Select(m =>
        {
            balance += m.QuantityKg;
            return new ProductMovementRow(m, balance);
        }).ToList();
    }

    public async Task<bool> HasUnreversedSalesOrDisposalsAsync(Guid lotId, IReadOnlyCollection<Guid> productIds, CancellationToken cancellationToken)
    {
        var products = productIds.ToList();
        return await db.ProductStockMovements.AnyAsync(
            m => m.HarvestLotId == lotId && products.Contains(m.ProductId) && (m.Kind == ProductStockMovementKind.Sale || m.Kind == ProductStockMovementKind.Disposal)
                && !db.ProductStockMovements.Any(r => r.ReversesMovementId == m.Id), cancellationToken);
    }

    public async Task<IReadOnlyList<ProductLotRow>> LotsOfProductAsync(Guid productId, IReadOnlySet<Guid>? zoneIds, CancellationToken cancellationToken)
    {
        var rows = await db.ProductStockMovements.AsNoTracking().Where(m => m.ProductId == productId && m.HarvestLotId != null).ToListAsync(cancellationToken);
        if (rows.Count == 0)
        {
            return [];
        }

        // A reversal counts under the kind of the row it undoes.
        var kindOf = rows.ToDictionary(r => r.Id, r => r.Kind);
        ProductStockMovementKind Effective(ProductStockMovement m) =>
            m.Kind == ProductStockMovementKind.Reversal && m.ReversesMovementId is { } undone && kindOf.TryGetValue(undone, out var original) ? original : m.Kind;

        var lotIds = rows.Select(r => r.HarvestLotId!.Value).Distinct().ToList();
        var lots = (await db.HarvestLots.AsNoTracking().Where(l => lotIds.Contains(l.Id))
            .Select(l => new { l.Id, l.HarvestDate, l.HarvestedAt, l.StatusId, RaftCode = l.Raft.Code, ZoneId = l.Raft.ZoneId, ZoneCode = l.Raft.Zone.Code }).ToListAsync(cancellationToken)).ToDictionary(l => l.Id);
        var inScope = zoneIds is null ? rows : rows.Where(r => lots.TryGetValue(r.HarvestLotId!.Value, out var l) && zoneIds.Contains(l.ZoneId)).ToList();
        return inScope.GroupBy(r => r.HarvestLotId!.Value).Select(g =>
        {
            decimal Sum(ProductStockMovementKind kind) => g.Where(r => Effective(r) == kind).Sum(r => r.QuantityKg);
            lots.TryGetValue(g.Key, out var lot);
            var code = g.OrderBy(r => r.Sequence).Select(r => r.LotCode).FirstOrDefault(c => c != null) ?? string.Empty;
            return new ProductLotRow(
                g.Key, code, lot?.ZoneId, lot?.ZoneCode, lot?.RaftCode, lot?.HarvestDate, lot?.HarvestedAt, lot?.StatusId,
                Sum(ProductStockMovementKind.Harvest), -Sum(ProductStockMovementKind.Sale), -Sum(ProductStockMovementKind.Disposal), Sum(ProductStockMovementKind.Return), g.Sum(r => r.QuantityKg));
        }).OrderByDescending(l => l.HarvestedAt).ThenByDescending(l => l.LotCode).ToList();
    }

    public void Add(ProductStockMovement movement) => db.ProductStockMovements.Add(movement);
}

internal sealed class ProductCategoryRepository(OmtsDbContext db) : IProductCategoryRepository
{
    public async Task<IReadOnlyList<ProductCategory>> ListAsync(CancellationToken cancellationToken) =>
        await db.ProductCategories.AsNoTracking().OrderBy(g => g.SortOrder).ThenBy(g => g.Name).ToListAsync(cancellationToken);

    public Task<ProductCategory?> GetForEditAsync(Guid categoryId, CancellationToken cancellationToken) =>
        db.ProductCategories.FirstOrDefaultAsync(g => g.Id == categoryId, cancellationToken);

    public async Task<IReadOnlyList<ProductCategory>> GetAllForEditAsync(CancellationToken cancellationToken) =>
        await db.ProductCategories.OrderBy(g => g.SortOrder).ThenBy(g => g.Name).ToListAsync(cancellationToken);

    public Task<ProductCategory?> FindByNameAsync(string name, Guid? exceptCategoryId, CancellationToken cancellationToken)
    {
        var upper = name.ToUpperInvariant();
        return db.ProductCategories.AsNoTracking().FirstOrDefaultAsync(g => g.Name.ToUpper() == upper && g.Id != exceptCategoryId, cancellationToken);
    }

    public async Task<IReadOnlyDictionary<Guid, int>> ProductCountsAsync(CancellationToken cancellationToken) =>
        await db.Products.AsNoTracking().Where(p => p.CategoryId != null && !p.IsHidden)
            .GroupBy(p => p.CategoryId!.Value).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, cancellationToken);

    public async Task<IReadOnlyList<Product>> HiddenProductsAsync(Guid categoryId, CancellationToken cancellationToken) =>
        await db.Products.Where(p => p.CategoryId == categoryId && p.IsHidden).ToListAsync(cancellationToken);

    public void Add(ProductCategory category) => db.ProductCategories.Add(category);

    public void Remove(ProductCategory category) => db.ProductCategories.Remove(category);
}

internal sealed class ProductDisposalRepository(OmtsDbContext db) : IProductDisposalRepository
{
    public Task<ProductDisposal?> GetForEditAsync(Guid disposalId, CancellationToken cancellationToken) =>
        db.ProductDisposals
            .Include(d => d.Zone)
            .Include(d => d.Lines).ThenInclude(l => l.Product)
            .Include(d => d.Lines).ThenInclude(l => l.HarvestLot)
            .AsSplitQuery()
            .FirstOrDefaultAsync(d => d.Id == disposalId, cancellationToken);

    public Task<ProductDisposal?> FindByCodeAsync(string code, Guid? exceptDisposalId, CancellationToken cancellationToken)
    {
        var upper = code.ToUpperInvariant();
        return db.ProductDisposals.AsNoTracking().FirstOrDefaultAsync(d => d.Code.ToUpper() == upper && d.Id != exceptDisposalId, cancellationToken);
    }

    public async Task<(IReadOnlyList<ProductDisposalRow> Rows, int Total, decimal TotalKg)> SearchAsync(ProductDisposalFilter filter, int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = Filtered(filter);
        var total = await query.CountAsync(cancellationToken);
        var ids = await Sorted(query, filter).Skip((page - 1) * pageSize).Take(pageSize).Select(f => f.Id).ToListAsync(cancellationToken);
        // The total kg counts the Completed disposals only; the kg of each match are read and added up here.
        var kgs = total == 0 ? [] : await query.Where(f => f.StatusId == StatusIds.ProductDisposal.Completed).Select(f => f.TotalKg).ToListAsync(cancellationToken);
        return (await RowsAsync(ids, cancellationToken), total, kgs.Sum());
    }

    public async Task<IReadOnlyList<ProductDisposalRow>> ListAsync(ProductDisposalFilter filter, CancellationToken cancellationToken) =>
        await RowsAsync(await Sorted(Filtered(filter), filter).Select(f => f.Id).ToListAsync(cancellationToken), cancellationToken);

    public Task<bool> IsLotOnDisposalAsync(Guid lotId, CancellationToken cancellationToken) =>
        // Every status counts: a cancelled disposal is still a document that names the lot, and its lines keep a foreign key to it.
        db.ProductDisposalLines.AnyAsync(l => l.HarvestLotId == lotId, cancellationToken);

    public void Add(ProductDisposal disposal) => db.ProductDisposals.Add(disposal);

    public void Remove(ProductDisposal disposal) => db.ProductDisposals.Remove(disposal);

    public void AddLine(ProductDisposalLine line) => db.ProductDisposalLines.Add(line);

    public void RemoveLine(ProductDisposalLine line) => db.ProductDisposalLines.Remove(line);

    private sealed class Figures
    {
        public Guid Id { get; init; }
        public string Code { get; init; } = string.Empty;
        public DateTimeOffset DisposedAt { get; init; }
        public int StatusId { get; init; }
        public decimal TotalKg { get; init; }
    }

    private async Task<IReadOnlyList<ProductDisposalRow>> RowsAsync(List<Guid> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        var disposals = (await db.ProductDisposals.AsNoTracking().Include(d => d.Zone).Where(d => ids.Contains(d.Id)).ToListAsync(cancellationToken)).ToDictionary(d => d.Id);
        var sums = await db.ProductDisposalLines.AsNoTracking().Where(l => ids.Contains(l.DisposalId))
            .GroupBy(l => l.DisposalId).Select(g => new { g.Key, Sum = g.Sum(l => l.Kg) }).ToListAsync(cancellationToken);
        return ids.Select(id => new ProductDisposalRow(disposals[id], sums.FirstOrDefault(s => s.Key == id)?.Sum ?? 0m)).ToList();
    }

    private IQueryable<Figures> Filtered(ProductDisposalFilter filter)
    {
        var disposals = db.ProductDisposals.AsNoTracking().AsQueryable();
        if (filter.ZoneIds is { } reach)
        {
            var zones = reach.ToList();
            disposals = disposals.Where(d => zones.Contains(d.ZoneId));
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var text = filter.Search.Trim();
            disposals = disposals.Where(d => d.Code.Contains(text) || (d.Notes != null && d.Notes.Contains(text)) || (d.Reason != null && d.Reason.Contains(text)));
        }

        if (filter.StatusIds is { Count: > 0 } statusIds)
        {
            disposals = disposals.Where(d => statusIds.Contains(d.StatusId));
        }

        if (filter.From is { } from)
        {
            disposals = disposals.Where(d => d.DisposedAt >= from);
        }

        if (filter.Before is { } before)
        {
            disposals = disposals.Where(d => d.DisposedAt < before);
        }

        if (filter.ZoneId is { } zoneId)
        {
            disposals = disposals.Where(d => d.ZoneId == zoneId);
        }

        if (filter.ProductId is { } productId)
        {
            disposals = disposals.Where(d => d.Lines.Any(l => l.ProductId == productId));
        }

        if (filter.CreatedById is { } createdBy)
        {
            disposals = disposals.Where(d => d.CreatedBy == createdBy);
        }

        return disposals.Select(d => new Figures
        {
            Id = d.Id, Code = d.Code, DisposedAt = d.DisposedAt, StatusId = d.StatusId,
            TotalKg = db.ProductDisposalLines.Where(l => l.DisposalId == d.Id).Sum(l => (decimal?)l.Kg) ?? 0m,
        });
    }

    private static IQueryable<Figures> Sorted(IQueryable<Figures> query, ProductDisposalFilter filter)
    {
        var descending = filter.Descending;
        IOrderedQueryable<Figures> Order<TKey>(System.Linq.Expressions.Expression<Func<Figures, TKey>> key) =>
            descending ? query.OrderByDescending(key) : query.OrderBy(key);
        var ordered = filter.Sort switch
        {
            ProductDisposalSort.DisposedAt => Order(f => f.DisposedAt),
            ProductDisposalSort.TotalKg => Order(f => f.TotalKg),
            ProductDisposalSort.Status => Order(f => f.StatusId),
            _ => Order(f => f.Code),
        };
        return ordered.ThenBy(f => f.Id);
    }
}
