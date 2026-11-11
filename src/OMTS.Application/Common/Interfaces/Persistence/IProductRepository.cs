using OMTS.Domain.Entities.Sales;

namespace OMTS.Application.Common.Interfaces.Persistence;

public interface IProductRepository
{
    /// <summary>The product with its units (and their catalogue rows), photos and group, tracked.</summary>
    Task<Product?> GetForEditAsync(Guid productId, CancellationToken cancellationToken);

    /// <summary>The products with their units, untracked.</summary>
    Task<IReadOnlyList<Product>> GetByIdsAsync(IReadOnlyCollection<Guid> productIds, CancellationToken cancellationToken);

    /// <summary>Another product whose name equals <paramref name="name"/> ignoring case, discontinued and deleted products included.</summary>
    Task<Product?> FindByNameAsync(string name, Guid? exceptProductId, CancellationToken cancellationToken);

    /// <summary>The product that owns a code, as a product code or as the code of one of its units (ignoring case), other than the given product and product unit.</summary>
    Task<CodeOwner?> FindCodeOwnerAsync(string code, Guid? exceptProductId, Guid? exceptProductUnitId, CancellationToken cancellationToken);

    /// <summary>The products that are not deleted, by code, name or a unit code (contains) and optional status, ordered by name; units and group loaded.</summary>
    Task<IReadOnlyList<Product>> ListAsync(string? search, int? statusId, CancellationToken cancellationToken);

    /// <summary>Whether any invoice line, purchase request, harvest line, disposal line or ledger row names the product: then it is hidden, not removed, when deleted.</summary>
    Task<bool> IsUsedAsync(Guid productId, CancellationToken cancellationToken);

    /// <summary>Which of the products are used (one query for a whole list).</summary>
    Task<IReadOnlySet<Guid>> UsedAmongAsync(IReadOnlyCollection<Guid> productIds, CancellationToken cancellationToken);

    /// <summary>The price changes of the product, newest first; of one unit (the shared unit id) when <paramref name="unitId"/> is given.</summary>
    Task<IReadOnlyList<ProductPriceChange>> PriceHistoryAsync(Guid productId, Guid? unitId, CancellationToken cancellationToken);

    void Add(Product product);

    /// <summary>A product nothing refers to is removed for good, with its units, photos and price history.</summary>
    void Remove(Product product);

    /// <summary>A new unit under a tracked product, added explicitly because its key is already set.</summary>
    void AddUnit(ProductUnit unit);

    void RemoveUnit(ProductUnit unit);

    void AddPhoto(ProductPhoto photo);

    void RemovePhoto(ProductPhoto photo);

    void AddPriceChange(ProductPriceChange change);

    /// <summary>The price history rows of a product that is removed for good, tracked.</summary>
    Task<IReadOnlyList<ProductPriceChange>> PriceHistoryForEditAsync(Guid productId, CancellationToken cancellationToken);

    void RemovePriceChange(ProductPriceChange change);
}
