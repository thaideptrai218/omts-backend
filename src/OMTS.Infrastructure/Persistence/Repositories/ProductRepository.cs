using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Infrastructure.Persistence.Repositories;

internal sealed class ProductRepository(OmtsDbContext db) : IProductRepository
{
    public Task<Product?> GetForEditAsync(Guid productId, CancellationToken cancellationToken) =>
        db.Products.FirstOrDefaultAsync(p => p.Id == productId, cancellationToken);

    public Task<Product?> FindByNameAsync(string name, Guid? exceptProductId, CancellationToken cancellationToken)
    {
        var upper = name.ToUpperInvariant();
        return db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Name.ToUpper() == upper && p.Id != exceptProductId, cancellationToken);
    }

    public async Task<IReadOnlyList<Product>> ListAsync(string? search, int? statusId, CancellationToken cancellationToken)
    {
        var query = db.Products.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var text = search.Trim();
            query = query.Where(p => p.Name.Contains(text));
        }

        if (statusId is not null)
        {
            query = query.Where(p => p.StatusId == statusId);
        }

        return await query.OrderBy(p => p.Name).ThenBy(p => p.Id).ToListAsync(cancellationToken);
    }

    public void Add(Product product) => db.Products.Add(product);
}
