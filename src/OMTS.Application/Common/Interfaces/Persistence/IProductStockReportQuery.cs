using OMTS.Domain.Enums;

namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>A product that has stock rows in the report window, with what the report shows of it.</summary>
public sealed record ProductStockProduct(Guid Id, string Code, string Name, Guid? CategoryId, string? CategoryName, decimal UnitPrice);

/// <summary>
/// The signed kg of the stock rows of one product in the period, by the kind they count as: a Reversal row that names the row it undoes counts as that row's kind (so a cancelled sale
/// takes its kg off the sold kg), a Reversal row that names none stays Reversal (an adjustment).
/// </summary>
public sealed record ProductStockFlow(Guid ProductId, ProductStockMovementKind Kind, decimal Kg);

/// <summary>What the report reads: the products, the kg on hand before the period and the flows in it. A product with no row at all is not in it.</summary>
public sealed record ProductStockFacts(
    IReadOnlyList<ProductStockProduct> Products, IReadOnlyDictionary<Guid, decimal> Opening, IReadOnlyList<ProductStockFlow> Flows);

/// <summary>Stock in, out and on hand per product (KiotViet "Xuất nhập tồn"), from the append-only product ledger. Read only; a fixed number of commands.</summary>
public interface IProductStockReportQuery
{
    /// <param name="from">First business day of the period (UTC+7).</param>
    /// <param name="to">Last business day of the period, included.</param>
    /// <param name="productIds">Only these products when not empty.</param>
    /// <param name="categoryIds">Only the products of these categories when not empty.</param>
    Task<ProductStockFacts> FactsAsync(
        DateOnly from, DateOnly to, IReadOnlyCollection<Guid> productIds, IReadOnlyCollection<Guid> categoryIds, CancellationToken cancellationToken);

    /// <summary>The names of the categories that exist among <paramref name="ids"/>.</summary>
    Task<IReadOnlyDictionary<Guid, string>> CategoryNamesAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);
}
