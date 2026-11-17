using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Infrastructure.Persistence.Repositories;

internal sealed class ProductRepository(OmtsDbContext db) : IProductRepository
{
    public Task<Product?> GetForEditAsync(Guid productId, CancellationToken cancellationToken) =>
        db.Products
            .Include(p => p.Units).ThenInclude(u => u.Unit)
            .Include(p => p.Photos)
            .Include(p => p.Category)
            .AsSplitQuery()
            .FirstOrDefaultAsync(p => p.Id == productId, cancellationToken);

    public async Task<IReadOnlyList<Product>> GetByIdsAsync(IReadOnlyCollection<Guid> productIds, CancellationToken cancellationToken)
    {
        var ids = productIds.ToList();
        return await db.Products.AsNoTracking().Include(p => p.Units).ThenInclude(u => u.Unit).Where(p => ids.Contains(p.Id)).AsSplitQuery().ToListAsync(cancellationToken);
    }

    public Task<Product?> FindByNameAsync(string name, Guid? exceptProductId, CancellationToken cancellationToken)
    {
        var upper = name.ToUpperInvariant();
        return db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Name.ToUpper() == upper && p.Id != exceptProductId, cancellationToken);
    }

    public async Task<CodeOwner?> FindCodeOwnerAsync(string code, Guid? exceptProductId, Guid? exceptProductUnitId, CancellationToken cancellationToken)
    {
        var upper = code.ToUpperInvariant();
        if (await db.Products.AsNoTracking().Where(p => p.Code.ToUpper() == upper && p.Id != exceptProductId).Select(p => (Guid?)p.Id).FirstOrDefaultAsync(cancellationToken) is { } productId)
        {
            return new CodeOwner(productId, false);
        }

        var unitOwner = await db.ProductUnits.AsNoTracking().Where(u => u.Code.ToUpper() == upper && u.Id != exceptProductUnitId).Select(u => (Guid?)u.ProductId).FirstOrDefaultAsync(cancellationToken);
        return unitOwner is { } owner ? new CodeOwner(owner, true) : null;
    }

    public async Task<IReadOnlyList<Product>> ListAsync(string? search, int? statusId, CancellationToken cancellationToken)
    {
        var query = db.Products.AsNoTracking().Where(p => !p.IsHidden);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var text = search.Trim();
            query = query.Where(p => p.Code.Contains(text) || p.Name.Contains(text) || p.Units.Any(u => u.Code.Contains(text)));
        }

        if (statusId is not null)
        {
            query = query.Where(p => p.StatusId == statusId);
        }

        return await query.Include(p => p.Units).ThenInclude(u => u.Unit).Include(p => p.Category).AsSplitQuery()
            .OrderBy(p => p.Name).ThenBy(p => p.Id).ToListAsync(cancellationToken);
    }

    public async Task<bool> IsUsedAsync(Guid productId, CancellationToken cancellationToken) =>
        (await UsedAmongAsync([productId], cancellationToken)).Contains(productId);

    public async Task<IReadOnlySet<Guid>> UsedAmongAsync(IReadOnlyCollection<Guid> productIds, CancellationToken cancellationToken)
    {
        if (productIds.Count == 0)
        {
            return new HashSet<Guid>();
        }

        var ids = productIds.ToList();
        var used = new HashSet<Guid>();
        used.UnionWith(await db.SalesInvoiceLines.Where(l => ids.Contains(l.ProductId)).Select(l => l.ProductId).Distinct().ToListAsync(cancellationToken));
        used.UnionWith(await db.SalesOrderLines.Where(l => ids.Contains(l.ProductId)).Select(l => l.ProductId).Distinct().ToListAsync(cancellationToken));
        used.UnionWith(await db.PurchaseRequests.Where(r => ids.Contains(r.ProductId)).Select(r => r.ProductId).Distinct().ToListAsync(cancellationToken));
        used.UnionWith(await db.HarvestLotProductLines.Where(l => ids.Contains(l.ProductId)).Select(l => l.ProductId).Distinct().ToListAsync(cancellationToken));
        used.UnionWith(await db.ProductDisposalLines.Where(l => ids.Contains(l.ProductId)).Select(l => l.ProductId).Distinct().ToListAsync(cancellationToken));
        used.UnionWith(await db.CultivationCycles.Where(c => c.ProductId != null && ids.Contains(c.ProductId.Value)).Select(c => c.ProductId!.Value).Distinct().ToListAsync(cancellationToken));
        used.UnionWith(await db.ProductStockMovements.Where(m => ids.Contains(m.ProductId)).Select(m => m.ProductId).Distinct().ToListAsync(cancellationToken));

        // A sale awaiting its VietQR transfer names its products in its JSON (ids in their "D" form): the transfer will invoice it, so the product must still exist.
        foreach (var id in ids.Where(id => !used.Contains(id)))
        {
            var text = id.ToString("D");
            if (await db.PendingSales.AnyAsync(s => s.Status == PendingSaleStatus.Awaiting && s.SaleJson.Contains(text), cancellationToken))
            {
                used.Add(id);
            }
        }

        return used;
    }

    public async Task<IReadOnlyList<ProductPriceChange>> PriceHistoryAsync(Guid productId, Guid? unitId, CancellationToken cancellationToken) =>
        await db.ProductPriceChanges.AsNoTracking().Where(c => c.ProductId == productId && (unitId == null || c.UnitId == unitId)).OrderByDescending(c => c.ChangedAt).ThenByDescending(c => c.Id).ToListAsync(cancellationToken);

    public void Add(Product product) => db.Products.Add(product);

    public void Remove(Product product) => db.Products.Remove(product);

    public void AddUnit(ProductUnit unit) => db.ProductUnits.Add(unit);

    public void RemoveUnit(ProductUnit unit) => db.ProductUnits.Remove(unit);

    public void AddPhoto(ProductPhoto photo) => db.ProductPhotos.Add(photo);

    public void RemovePhoto(ProductPhoto photo) => db.ProductPhotos.Remove(photo);

    public void AddPriceChange(ProductPriceChange change) => db.ProductPriceChanges.Add(change);

    public async Task<IReadOnlyList<ProductPriceChange>> PriceHistoryForEditAsync(Guid productId, CancellationToken cancellationToken) =>
        await db.ProductPriceChanges.Where(c => c.ProductId == productId).ToListAsync(cancellationToken);

    public void RemovePriceChange(ProductPriceChange change) => db.ProductPriceChanges.Remove(change);
}
