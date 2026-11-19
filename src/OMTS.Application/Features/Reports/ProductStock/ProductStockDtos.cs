namespace OMTS.Application.Features.Reports.ProductStock;

/// <summary>The groupings of the product stock report.</summary>
public enum ProductStockGrouping
{
    Product,
    Category,
}

/// <summary>
/// The query of the report and its export. Dates are Vietnam dates, the current month when none is given. <c>GroupBy</c> is product (default) or category; <c>ProductIds</c> and
/// <c>CategoryIds</c> are repeated query values that narrow the products.
/// </summary>
public sealed record ProductStockQuery(DateOnly? From = null, DateOnly? To = null, string? GroupBy = null, Guid[]? ProductIds = null, Guid[]? CategoryIds = null);

/// <summary>The choices that were checked and applied.</summary>
public sealed record ProductStockChoices(IReadOnlyList<ReportChoice> Products, IReadOnlyList<ReportChoice> Categories);

/// <summary>
/// The kg of a group or of the total: <c>OpeningKg</c> on hand before the first day; <c>ReceivedKg</c> harvested and taken back in the period; <c>SoldKg</c> sold (positive); <c>DisposedKg</c> thrown away
/// or written off with a lot (positive); <c>AdjustmentKg</c> signed, reversal rows that undo no named row; <c>ClosingKg</c> = opening + received − sold − disposed + adjustment, on hand at the end of the last day.
/// <c>ClosingValue</c> is the closing kg at the current list price (whole VND), not a cost.
/// </summary>
public sealed record ProductStockFigures(
    decimal OpeningKg, decimal ReceivedKg, decimal SoldKg, decimal DisposedKg, decimal AdjustmentKg, decimal ClosingKg, decimal ClosingValue);

/// <param name="Key">The product id, the category id, or <c>uncategorized</c>.</param>
/// <param name="Code">The product code; null for a category.</param>
/// <param name="UnitPrice">The list price of one kg; null for a category.</param>
public sealed record ProductStockGroup(
    string Key, string Label, string? Code, decimal? UnitPrice,
    decimal OpeningKg, decimal ReceivedKg, decimal SoldKg, decimal DisposedKg, decimal AdjustmentKg, decimal ClosingKg, decimal ClosingValue);

/// <summary>The report worked out when it is shown; <see cref="Message"/> is MSG-GEN-10 when no product has stock rows, the groups are then empty and the totals 0.</summary>
public sealed record ProductStockReport(
    DateTimeOffset ComputedAt, DateOnly From, DateOnly To, string GroupBy, ProductStockChoices Filters, IReadOnlyList<ProductStockGroup> Groups,
    ProductStockFigures Totals, string? Message, string? MessageCode);
