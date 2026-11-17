using OMTS.Application.Features.Harvest;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Application.Features.Sales.Orders;

/// <summary>
/// The line of an order made from a trader's purchase request. A request is in whole cages (decision of 08/11/2026), and an order counts a product in one of its units: the cages go on the Lồng unit
/// of the product when it has one; otherwise the line is in kg, the cages times the average kg per cage of the cooperative. The price is the sale price of that unit.
/// </summary>
internal static class SalesOrderRequestLine
{
    /// <summary>The line, or null when the product has no unit to count in (no base unit): such a request cannot be turned into an order line.</summary>
    /// <param name="cages">The cages the Owner accepted, else the cages asked.</param>
    /// <param name="product">The product with its units and their shared unit loaded.</param>
    public static SalesOrderDraftLine? Of(Product product, decimal cages, decimal kgPerCage)
    {
        var cageUnit = product.Units.FirstOrDefault(u => !u.IsBase && HarvestLotProductLineService.IsCageUnit(u.Unit.Name));
        if (cageUnit is not null)
        {
            return Line(product, cageUnit, cages);
        }

        var baseUnit = product.Units.FirstOrDefault(u => u.IsBase);
        return baseUnit is null ? null : Line(product, baseUnit, decimal.Round(cages * kgPerCage, 3));
    }

    private static SalesOrderDraftLine Line(Product product, ProductUnit unit, decimal quantity) =>
        new(product.Id, product.Code, product.Name, unit.UnitId, unit.Code, unit.Unit.Name, quantity, unit.SalePrice);
}
