using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;

namespace OMTS.Infrastructure.Persistence.Queries;

/// <summary>The product list: the products that are not deleted, with the kg on hand summed from the product ledger by SQL.</summary>
internal sealed class ProductListReader(OmtsDbContext db) : IProductListQuery
{
    private sealed class Figures
    {
        public Guid Id { get; init; }
        public string Code { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string? CategoryName { get; init; }
        public int StatusId { get; init; }
        public decimal UnitPrice { get; init; }
        public decimal? MinKg { get; init; }
        public decimal? MaxKg { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public decimal OnHand { get; init; }
    }

    public async Task<(IReadOnlyList<ProductRow> Rows, int Total, decimal TotalOnHandKg)> SearchAsync(ProductFilter filter, int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = Filtered(filter);
        var total = await query.CountAsync(cancellationToken);
        var ids = await Sorted(query, filter).Skip((page - 1) * pageSize).Take(pageSize).Select(f => f.Id).ToListAsync(cancellationToken);
        // SQL Server cannot sum an expression that holds a subquery: the kg of each match are read and added up here.
        var kgs = total == 0 ? [] : await query.Select(f => f.OnHand).ToListAsync(cancellationToken);
        return (await RowsAsync(ids, cancellationToken), total, kgs.Sum());
    }

    public async Task<IReadOnlyList<ProductRow>> ListAsync(ProductFilter filter, CancellationToken cancellationToken) =>
        await RowsAsync(await Sorted(Filtered(filter), filter).Select(f => f.Id).ToListAsync(cancellationToken), cancellationToken);

    private async Task<IReadOnlyList<ProductRow>> RowsAsync(List<Guid> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        var products = (await db.Products.AsNoTracking()
            .Include(p => p.Units).ThenInclude(u => u.Unit)
            .Include(p => p.Category)
            .Include(p => p.Photos)
            .Where(p => ids.Contains(p.Id)).AsSplitQuery().ToListAsync(cancellationToken)).ToDictionary(p => p.Id);
        var sums = await db.ProductStockMovements.AsNoTracking().Where(m => ids.Contains(m.ProductId))
            .GroupBy(m => m.ProductId).Select(g => new { g.Key, Sum = g.Sum(m => m.QuantityKg) }).ToListAsync(cancellationToken);
        return ids.Select(id => new ProductRow(products[id], sums.FirstOrDefault(s => s.Key == id)?.Sum ?? 0m)).ToList();
    }

    private IQueryable<Figures> Filtered(ProductFilter filter)
    {
        var products = db.Products.AsNoTracking().Where(p => !p.IsHidden);
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var text = filter.Search.Trim();
            products = products.Where(p => p.Code.Contains(text) || p.Name.Contains(text) || p.Units.Any(u => u.Code.Contains(text)));
        }

        if (filter.CategoryId is { } categoryId)
        {
            // A parent group also lists the products of its child groups.
            products = products.Where(p => p.CategoryId == categoryId || (p.Category != null && p.Category.ParentId == categoryId));
        }

        if (filter.StatusId is { } statusId)
        {
            products = products.Where(p => p.StatusId == statusId);
        }

        var figures = products.Select(p => new Figures
        {
            Id = p.Id, Code = p.Code, Name = p.Name, CategoryName = p.Category != null ? p.Category.Name : null, StatusId = p.StatusId, UnitPrice = p.UnitPrice,
            MinKg = p.MinKg, MaxKg = p.MaxKg, CreatedAt = p.CreatedAt,
            OnHand = db.ProductStockMovements.Where(m => m.ProductId == p.Id).Sum(m => (decimal?)m.QuantityKg) ?? 0m,
        });
        return filter.Stock switch
        {
            ProductStockState.InStock => figures.Where(f => f.OnHand > 0),
            ProductStockState.OutOfStock => figures.Where(f => f.OnHand <= 0),
            ProductStockState.BelowMin => figures.Where(f => f.MinKg != null && f.OnHand < f.MinKg),
            ProductStockState.AboveMax => figures.Where(f => f.MaxKg != null && f.OnHand > f.MaxKg),
            _ => figures,
        };
    }

    private static IQueryable<Figures> Sorted(IQueryable<Figures> query, ProductFilter filter)
    {
        var descending = filter.Descending;
        IOrderedQueryable<Figures> Order<TKey>(System.Linq.Expressions.Expression<Func<Figures, TKey>> key) =>
            descending ? query.OrderByDescending(key) : query.OrderBy(key);
        var ordered = filter.Sort switch
        {
            ProductSort.Name => Order(f => f.Name),
            ProductSort.Category => Order(f => f.CategoryName),
            ProductSort.UnitPrice => Order(f => f.UnitPrice),
            ProductSort.OnHand => Order(f => f.OnHand),
            ProductSort.CreatedAt => Order(f => f.CreatedAt),
            ProductSort.Status => Order(f => f.StatusId),
            _ => Order(f => f.Code),
        };
        return ordered.ThenBy(f => f.Id);
    }
}
