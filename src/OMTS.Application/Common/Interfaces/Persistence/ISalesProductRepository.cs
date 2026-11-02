using OMTS.Domain.Entities.Sales;

namespace OMTS.Application.Common.Interfaces.Persistence;

public interface ISalesProductRepository
{
    Task<IReadOnlyList<Product>> ListAsync(bool activeOnly, DateOnly today, CancellationToken ct);
    Task<Product?> GetAsync(Guid id, CancellationToken ct);
    Task<Product?> FindCodeAsync(string code, Guid? exceptId, CancellationToken ct);
    void Add(Product product);
}
