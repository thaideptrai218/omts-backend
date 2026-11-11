using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Purchasing.Items;

/// <summary>The cost of an input item after a movement, as pure functions.</summary>
public static class InputCostRules
{
    /// <summary>
    /// Fixed: only a receipt of an item with no cost sets it, to the price of that receipt. Average: a receipt, a return and the reversals of them move the average
    /// <c>(qtyBefore × cost + quantity × price) ÷ (qtyBefore + quantity)</c> with the quantity on hand floored at 0 and a cost of none counting as the price; when nothing would be left
    /// the cost stays. Opening, issue and disposal rows, and rows with no price, leave the cost as it is. The result has at most 4 decimals.
    /// </summary>
    /// <param name="currentCost">The cost of one base unit before the movement; null when none was set.</param>
    /// <param name="quantityOnHand">The quantity on hand before the movement.</param>
    /// <param name="quantity">Signed: positive adds to the stock.</param>
    /// <param name="price">The price of one base unit on the document; null on rows with none.</param>
    public static decimal? Adjusted(InputCostMethod method, InputStockMovementKind kind, decimal? currentCost, decimal quantityOnHand, decimal quantity, decimal? price)
    {
        if (price is not { } p)
        {
            return currentCost;
        }

        if (method == InputCostMethod.Fixed)
        {
            return kind == InputStockMovementKind.Receipt && currentCost is null ? p : currentCost;
        }

        if (kind is InputStockMovementKind.Issue or InputStockMovementKind.Disposal or InputStockMovementKind.Opening)
        {
            return currentCost;
        }

        var before = Math.Max(0m, quantityOnHand);
        var after = before + quantity;
        if (after <= 0m)
        {
            return currentCost ?? (quantity > 0 ? p : null);
        }

        var average = (before * (currentCost ?? p) + quantity * p) / after;
        return decimal.Round(Math.Max(0m, average), 4);
    }
}
