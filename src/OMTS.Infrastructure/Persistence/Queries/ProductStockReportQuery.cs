using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Enums;

namespace OMTS.Infrastructure.Persistence.Queries;

/// <summary>
/// The product stock report on the ledger <c>ProductStockMovements</c>: the kg before the first day (UTC+7 midnight) and the flows by kind up to the end of the last day. A Reversal row
/// that names the row it undoes takes that row's kind. Read only.
/// </summary>
internal sealed class ProductStockReportQuery(OmtsDbContext db) : IProductStockReportQuery
{
    public async Task<ProductStockFacts> FactsAsync(
        DateOnly from, DateOnly to, IReadOnlyCollection<Guid> productIds, IReadOnlyCollection<Guid> categoryIds, CancellationToken cancellationToken)
    {
        var start = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), BusinessClock.UtcOffset);
        var endExclusive = new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), BusinessClock.UtcOffset);
        var products = productIds.ToList();
        var categories = categoryIds.ToList();
        var moves = db.ProductStockMovements.AsNoTracking().Where(m => m.OccurredAt < endExclusive);
        if (products.Count > 0)
        {
            moves = moves.Where(m => products.Contains(m.ProductId));
        }

        if (categories.Count > 0)
        {
            moves = moves.Where(m => m.Product.CategoryId != null && categories.Contains(m.Product.CategoryId.Value));
        }

        var opening = await moves.Where(m => m.OccurredAt < start)
            .GroupBy(m => m.ProductId)
            .Select(g => new { ProductId = g.Key, Kg = g.Sum(m => m.QuantityKg) })
            .ToDictionaryAsync(x => x.ProductId, x => x.Kg, cancellationToken);
        var flows = await (
                from m in moves.Where(m => m.OccurredAt >= start)
                join o in db.ProductStockMovements.AsNoTracking() on m.ReversesMovementId equals o.Id into originals
                from o in originals.DefaultIfEmpty()
                select new { m.ProductId, Kind = m.Kind == ProductStockMovementKind.Reversal && o != null ? o.Kind : m.Kind, m.QuantityKg })
            .GroupBy(x => new { x.ProductId, x.Kind })
            .Select(g => new { g.Key.ProductId, g.Key.Kind, Kg = g.Sum(x => x.QuantityKg) })
            .ToListAsync(cancellationToken);

        var ids = opening.Keys.Concat(flows.Select(f => f.ProductId)).Distinct().ToList();
        var found = (await db.Products.AsNoTracking()
                .Where(p => ids.Contains(p.Id))
                .Select(p => new { p.Id, p.Code, p.Name, p.CategoryId, CategoryName = p.Category == null ? null : p.Category.Name, p.UnitPrice })
                .ToListAsync(cancellationToken))
            .Select(p => new ProductStockProduct(p.Id, p.Code, p.Name, p.CategoryId, p.CategoryName, p.UnitPrice))
            .ToList();
        return new ProductStockFacts(found, opening, flows.Select(f => new ProductStockFlow(f.ProductId, f.Kind, f.Kg)).ToList());
    }

    public async Task<IReadOnlyDictionary<Guid, string>> CategoryNamesAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        var wanted = ids.ToList();
        return wanted.Count == 0
            ? new Dictionary<Guid, string>()
            : await db.ProductCategories.AsNoTracking().Where(c => wanted.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, cancellationToken);
    }
}
