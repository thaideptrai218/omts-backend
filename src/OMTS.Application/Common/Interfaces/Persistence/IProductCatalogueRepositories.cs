using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>The kg ledger of the products (append-only), and the reads of it that the catalogue shows.</summary>
public interface IProductStockRepository
{
    /// <summary>The kg on hand (Σ movements) of each product; a product with no row is 0.</summary>
    Task<IReadOnlyDictionary<Guid, decimal>> OnHandAsync(IReadOnlyCollection<Guid> productIds, CancellationToken cancellationToken);

    /// <summary>The rows of a document (a lot, an invoice or a disposal) that no reversal row undoes yet, in write order; untracked.</summary>
    Task<IReadOnlyList<ProductStockMovement>> UnreversedOfDocumentAsync(Guid documentId, CancellationToken cancellationToken);

    /// <summary>The ledger of each harvest lot for the products: the key holds the lot; rows with no lot are absent.</summary>
    Task<IReadOnlyDictionary<(Guid ProductId, Guid LotId), decimal>> LotBalancesAsync(IReadOnlyCollection<Guid> productIds, IReadOnlyCollection<Guid> lotIds, CancellationToken cancellationToken);

    /// <summary>The movements of a product oldest first with the balance after each, in the order of <c>OccurredAt</c> then write order. Without reversals the rows an edit or cancellation undid and the reversal rows are left out together.</summary>
    /// <param name="zoneIds">Only the rows on harvest lots of these zones (a zone-scoped reader); null: every row. The balance is then the balance of those rows.</param>
    Task<IReadOnlyList<ProductMovementRow>> ForProductAsync(Guid productId, bool includeReversals, IReadOnlySet<Guid>? zoneIds, CancellationToken cancellationToken);

    /// <summary>Every ledger row of the product, with the lot it names (zone, status and harvest date of the lot when it still exists).</summary>
    /// <param name="zoneIds">Only the lots of these zones; null: every lot.</param>
    Task<IReadOnlyList<ProductLotRow>> LotsOfProductAsync(Guid productId, IReadOnlySet<Guid>? zoneIds, CancellationToken cancellationToken);

    /// <summary>Whether any of the products has a Sale or Disposal row on the lot that no reversal row undoes.</summary>
    Task<bool> HasUnreversedSalesOrDisposalsAsync(Guid lotId, IReadOnlyCollection<Guid> productIds, CancellationToken cancellationToken);

    void Add(ProductStockMovement movement);
}

public sealed record ProductMovementRow(ProductStockMovement Movement, decimal BalanceAfter);

/// <summary>The ledger of a product on one harvest lot, split by what wrote it; reversals count under the kind of the row they undo.</summary>
public sealed record ProductLotRow(
    Guid LotId, string LotCode, Guid? ZoneId, string? ZoneCode, string? RaftCode, DateOnly? HarvestDate, DateTimeOffset? HarvestedAt, int? LotStatusId, decimal HarvestedKg, decimal SoldKg, decimal DisposedKg, decimal ReturnedKg, decimal RemainingKg);

public enum ProductStockState
{
    All,
    InStock,
    OutOfStock,
    BelowMin,
    AboveMax,
}

public enum ProductSort
{
    Code,
    Name,
    Category,
    UnitPrice,
    OnHand,
    CreatedAt,
    Status,
}

/// <summary>Filters of the product list; null means "any".</summary>
public sealed record ProductFilter(string? Search, Guid? CategoryId, ProductStockState Stock, int? StatusId, ProductSort Sort, bool Descending);

/// <summary>A product of the list with the kg computed in SQL.</summary>
public sealed record ProductRow(Product Product, decimal OnHandKg);

public interface IProductListQuery
{
    /// <summary>One page (1-based) of the matching products, and the total and the Σ kg on hand of every match.</summary>
    Task<(IReadOnlyList<ProductRow> Rows, int Total, decimal TotalOnHandKg)> SearchAsync(ProductFilter filter, int page, int pageSize, CancellationToken cancellationToken);

    Task<IReadOnlyList<ProductRow>> ListAsync(ProductFilter filter, CancellationToken cancellationToken);
}

public interface IProductCategoryRepository
{
    Task<IReadOnlyList<ProductCategory>> ListAsync(CancellationToken cancellationToken);
    Task<ProductCategory?> GetForEditAsync(Guid categoryId, CancellationToken cancellationToken);
    Task<IReadOnlyList<ProductCategory>> GetAllForEditAsync(CancellationToken cancellationToken);
    Task<ProductCategory?> FindByNameAsync(string name, Guid? exceptCategoryId, CancellationToken cancellationToken);

    /// <summary>How many products (not deleted) belong to each group; a group with none is absent.</summary>
    Task<IReadOnlyDictionary<Guid, int>> ProductCountsAsync(CancellationToken cancellationToken);

    /// <summary>The deleted products of the group, tracked: they let go of it when the group is deleted.</summary>
    Task<IReadOnlyList<Product>> HiddenProductsAsync(Guid categoryId, CancellationToken cancellationToken);

    void Add(ProductCategory category);
    void Remove(ProductCategory category);
}

/// <summary>Filters of the disposal list; null means "any". The caller's reach is <paramref name="ZoneIds"/> (null: every zone).</summary>
public sealed record ProductDisposalFilter(
    string? Search, IReadOnlyList<int>? StatusIds, DateTimeOffset? From, DateTimeOffset? Before, Guid? ZoneId, Guid? ProductId, Guid? CreatedById,
    IReadOnlySet<Guid>? ZoneIds, ProductDisposalSort Sort, bool Descending);

public enum ProductDisposalSort
{
    Code,
    DisposedAt,
    TotalKg,
    Status,
}

public sealed record ProductDisposalRow(ProductDisposal Disposal, decimal TotalKg);

public interface IProductDisposalRepository
{
    /// <summary>The disposal with its zone and lines (product, unit and lot), tracked.</summary>
    Task<ProductDisposal?> GetForEditAsync(Guid disposalId, CancellationToken cancellationToken);

    Task<ProductDisposal?> FindByCodeAsync(string code, Guid? exceptDisposalId, CancellationToken cancellationToken);

    Task<(IReadOnlyList<ProductDisposalRow> Rows, int Total, decimal TotalKg)> SearchAsync(ProductDisposalFilter filter, int page, int pageSize, CancellationToken cancellationToken);

    Task<IReadOnlyList<ProductDisposalRow>> ListAsync(ProductDisposalFilter filter, CancellationToken cancellationToken);

    /// <summary>Whether a disposal that is not Cancelled names the lot: the lot cannot be removed then.</summary>
    Task<bool> IsLotOnDisposalAsync(Guid lotId, CancellationToken cancellationToken);

    void Add(ProductDisposal disposal);
    void Remove(ProductDisposal disposal);
    void AddLine(ProductDisposalLine line);
    void RemoveLine(ProductDisposalLine line);
}
