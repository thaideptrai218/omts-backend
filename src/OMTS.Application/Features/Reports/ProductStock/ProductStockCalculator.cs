using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Reports.ProductStock;

/// <summary>
/// The stock figures as pure functions: received = harvests and returns, sold = sales, disposed = disposals and lot closes (both shown positive), the rest of the reversals is the adjustment.
/// Closing = opening + received − sold − disposed + adjustment, so the closing kg of a product is what the ledger sums to at the end of the period (the kg on hand of the product screen). A product
/// with no kg and no flow is left out.
/// </summary>
public static class ProductStockCalculator
{
    public const string UncategorizedKey = "uncategorized";
    public const string UncategorizedLabel = "No category";

    public static (IReadOnlyList<ProductStockGroup> Groups, ProductStockFigures Totals) Build(ProductStockFacts facts, ProductStockGrouping grouping)
    {
        var flowsOf = facts.Flows.ToLookup(f => f.ProductId);
        var rows = new List<(ProductStockProduct Product, ProductStockFigures Figures)>();
        foreach (var product in facts.Products)
        {
            var flows = flowsOf[product.Id].ToList();
            decimal Sum(params ProductStockMovementKind[] kinds) => flows.Where(f => kinds.Contains(f.Kind)).Sum(f => f.Kg);
            var opening = facts.Opening.GetValueOrDefault(product.Id);
            var (received, sold, disposed, adjustment) = (
                Sum(ProductStockMovementKind.Harvest, ProductStockMovementKind.Return),
                -Sum(ProductStockMovementKind.Sale),
                -Sum(ProductStockMovementKind.Disposal, ProductStockMovementKind.LotClose),
                Sum(ProductStockMovementKind.Reversal));
            var closing = opening + received - sold - disposed + adjustment;
            if (opening == 0m && received == 0m && sold == 0m && disposed == 0m && adjustment == 0m && closing == 0m)
            {
                continue;
            }

            rows.Add((product, new ProductStockFigures(opening, received, sold, disposed, adjustment, closing, Math.Round(closing * product.UnitPrice, 0, MidpointRounding.AwayFromZero))));
        }

        var groups = (grouping == ProductStockGrouping.Product
                ? rows.Select(r => new ProductStockGroup(r.Product.Id.ToString(), r.Product.Name, r.Product.Code, r.Product.UnitPrice, r.Figures.OpeningKg, r.Figures.ReceivedKg,
                    r.Figures.SoldKg, r.Figures.DisposedKg, r.Figures.AdjustmentKg, r.Figures.ClosingKg, r.Figures.ClosingValue))
                : rows.GroupBy(r => r.Product.CategoryId is { } id ? (Key: id.ToString(), Label: r.Product.CategoryName ?? string.Empty) : (Key: UncategorizedKey, Label: UncategorizedLabel))
                    .Select(g => new ProductStockGroup(g.Key.Key, g.Key.Label, null, null, g.Sum(r => r.Figures.OpeningKg), g.Sum(r => r.Figures.ReceivedKg), g.Sum(r => r.Figures.SoldKg),
                        g.Sum(r => r.Figures.DisposedKg), g.Sum(r => r.Figures.AdjustmentKg), g.Sum(r => r.Figures.ClosingKg), g.Sum(r => r.Figures.ClosingValue))))
            .OrderBy(g => g.Label, StringComparer.CurrentCulture)
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .ToList();
        var totals = new ProductStockFigures(
            groups.Sum(g => g.OpeningKg), groups.Sum(g => g.ReceivedKg), groups.Sum(g => g.SoldKg), groups.Sum(g => g.DisposedKg), groups.Sum(g => g.AdjustmentKg),
            groups.Sum(g => g.ClosingKg), groups.Sum(g => g.ClosingValue));
        return (groups, totals);
    }
}
