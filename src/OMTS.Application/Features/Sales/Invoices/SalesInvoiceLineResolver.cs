using OMTS.Application.Common.Results;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Application.Features.Sales.Invoices;

/// <summary>The unit of a line with what is read from it: its price list entry and whether it is the kg unit of the product.</summary>
/// <param name="SalePrice">The price of one of the unit on the price list, or (a unit that left the product since) the list price the invoice line kept.</param>
internal sealed record ResolvedUnit(Guid UnitId, string Code, string Name, decimal Factor, bool IsBase, decimal SalePrice);

/// <summary>A line with its unit, its quantity in that unit and the order line it delivers.</summary>
internal sealed record ResolvedLine(ResolvedUnit Unit, decimal Quantity, SalesOrderLine? OrderLine);

/// <summary>
/// The unit, the quantity and the order line of each line of an invoice, as pure rules: a line without a unit counts in the kg unit of its product, and the kg weighed are then its quantity; a
/// line in another unit names its quantity as well. A line that names an order line must name a line of the order, of the same product and the same unit.
/// </summary>
internal static class SalesInvoiceLineResolver
{
    /// <param name="existing">The lines the invoice already has (an edit): a unit that left the product since stays usable for the lines that already use it.</param>
    public static Result<IReadOnlyDictionary<int, ResolvedLine>> Resolve(
        IReadOnlyList<SalesInvoiceLineEntries> lines, IReadOnlyDictionary<Guid, Product> products, SalesOrder? order, IReadOnlyCollection<SalesInvoiceLine> existing)
    {
        var resolved = new Dictionary<int, ResolvedLine>();
        foreach (var entry in lines)
        {
            var product = products[entry.ProductId];
            var unit = UnitOf(product, entry.UnitId, existing);
            if (unit is null)
            {
                return AppError.Invalid($"Line {entry.LineNumber} unit");
            }

            if (!unit.IsBase && entry.Quantity is null)
            {
                return AppError.Invalid($"Line {entry.LineNumber} quantity");
            }

            SalesOrderLine? orderLine = null;
            if (entry.OrderLineId is { } orderLineId)
            {
                orderLine = order?.Lines.FirstOrDefault(l => l.Id == orderLineId);
                if (orderLine is null || orderLine.ProductId != entry.ProductId || orderLine.UnitId != unit.UnitId)
                {
                    return AppError.Invalid($"Line {entry.LineNumber} order line");
                }
            }

            resolved[entry.LineNumber] = new ResolvedLine(unit, unit.IsBase ? entry.Kg : entry.Quantity!.Value, orderLine);
        }

        return Result<IReadOnlyDictionary<int, ResolvedLine>>.Success(resolved);
    }

    private static ResolvedUnit? UnitOf(Product product, Guid? unitId, IReadOnlyCollection<SalesInvoiceLine> existing)
    {
        var onProduct = unitId is null ? product.Units.FirstOrDefault(u => u.IsBase) : product.Units.FirstOrDefault(u => u.UnitId == unitId);
        if (onProduct is not null)
        {
            return new ResolvedUnit(onProduct.UnitId, onProduct.Code, onProduct.Unit?.Name ?? onProduct.Code, onProduct.Factor, onProduct.IsBase, onProduct.SalePrice);
        }

        // The kg unit of a product with no unit list (nothing is seeded for it) is not guessed; a unit that left the product stays for the lines that already count in it.
        if (unitId is { } kept && existing.FirstOrDefault(l => l.ProductId == product.Id && l.UnitId == kept) is { } line)
        {
            return new ResolvedUnit(kept, line.UnitCode, line.UnitName, line.Factor, line.Quantity == line.Kg && line.Factor == 1m, line.ListPrice);
        }

        return null;
    }
}
