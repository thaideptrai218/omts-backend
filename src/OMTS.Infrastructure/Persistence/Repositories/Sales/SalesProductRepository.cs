using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Infrastructure.Persistence.Repositories.Sales;

internal sealed class SalesProductRepository(OmtsDbContext db) : ISalesProductRepository
{
    public async Task<IReadOnlyList<Product>> ListAsync(bool activeOnly, DateOnly today, CancellationToken ct) =>
        await db.Products.Where(p => !activeOnly || (p.StatusId == StatusIds.Usage.InUse &&
            (p.ValidFrom == null || p.ValidFrom <= today) && (p.ValidTo == null || p.ValidTo >= today)))
            .OrderBy(p => p.Name).ToListAsync(ct);
    public Task<Product?> GetAsync(Guid id, CancellationToken ct) => db.Products.FirstOrDefaultAsync(p => p.Id == id, ct);
    public Task<Product?> FindCodeAsync(string code, Guid? exceptId, CancellationToken ct) =>
        db.Products.FirstOrDefaultAsync(p => p.Code == code && (exceptId == null || p.Id != exceptId), ct);
    public void Add(Product product) => db.Products.Add(product);
}
