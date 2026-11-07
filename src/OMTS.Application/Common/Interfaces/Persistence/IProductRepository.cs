using OMTS.Domain.Entities.Sales;

namespace OMTS.Application.Common.Interfaces.Persistence;

public interface IProductRepository
{
    Task<Product?> GetForEditAsync(Guid productId, CancellationToken cancellationToken);

    /// <summary>Another product whose name equals <paramref name="name"/> ignoring case, discontinued products included.</summary>
    Task<Product?> FindByNameAsync(string name, Guid? exceptProductId, CancellationToken cancellationToken);

    /// <summary>By name (contains) and optional status, ordered by name.</summary>
    Task<IReadOnlyList<Product>> ListAsync(string? search, int? statusId, CancellationToken cancellationToken);

    void Add(Product product);
}
