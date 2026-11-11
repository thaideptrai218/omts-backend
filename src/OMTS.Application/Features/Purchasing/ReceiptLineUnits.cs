using OMTS.Domain.Entities.Purchasing;

namespace OMTS.Application.Features.Purchasing;

/// <summary>
/// Lines are typed in a unit of the item (a bag, a box) and kept in base units: the quantity, the unit price and the discount of the line are per base unit (what seed lots, returns
/// and the movement ledger count in), while the values as typed and the factor they were converted with stay on the line, and the amount of the line is worked out from the typed values.
/// </summary>
public static class ReceiptLineUnits
{
    /// <summary>The unit of the item a line names: the base unit when <paramref name="itemUnitId"/> is empty, null when the item has no such unit.</summary>
    public static InputItemUnit? Resolve(FarmingInputItem item, Guid? itemUnitId) =>
        itemUnitId is { } id ? item.Units.FirstOrDefault(u => u.Id == id) : item.Units.FirstOrDefault(u => u.IsBase);

    /// <summary>A price (or discount) per unit of the line as a whole-dong price per base unit, half up.</summary>
    public static decimal PricePerBase(decimal entered, decimal factor) => PurchaseMoney.RoundHalfUp(entered / factor);

    /// <summary>What one base unit cost on the line, exact to 4 decimals: the typed price less the typed discount, divided by the factor; 0 while the line has no price.</summary>
    public static decimal NetPricePerBase(PurchaseReceiptLine line) =>
        decimal.Round(((line.EnteredUnitPrice ?? 0m) - line.EnteredDiscount) / line.UnitFactor, 4);

    /// <summary>Sets the unit of a line and the figures that depend on it; the item may be null on a Draft line that has none yet.</summary>
    public static void SetUnit(PurchaseReceiptLine line, FarmingInputItem? item, InputItemUnit? unit)
    {
        line.ItemUnitId = unit?.Id;
        line.UnitFactor = unit?.Factor ?? 1m;
        line.Unit = unit?.Unit?.Name ?? item?.Unit ?? string.Empty;
    }

    /// <summary>Sets the unit of a return line from the receipt line it returns (the same unit and factor) or from the unit chosen on a return with no receipt, and the typed values with everything worked out from them.</summary>
    public static void FillReturn(PurchaseReturnLine line, string unitName, Guid? itemUnitId, decimal factor, decimal quantity, decimal unitPrice)
    {
        line.ItemUnitId = itemUnitId;
        line.UnitFactor = factor;
        line.Unit = unitName;
        SetReturnValues(line, quantity, unitPrice);
    }

    /// <summary>Sets the typed quantity and price of a return line in the unit it has, and the base quantity, the base price and the value from them.</summary>
    public static void SetReturnValues(PurchaseReturnLine line, decimal quantity, decimal unitPrice)
    {
        line.EnteredQuantity = quantity;
        line.EnteredUnitPrice = unitPrice;
        line.Quantity = quantity * line.UnitFactor;
        line.UnitPrice = PricePerBase(unitPrice, line.UnitFactor);
        line.ReturnValue = PurchaseMoney.LineAmount(quantity, unitPrice);
    }

    /// <summary>Sets the typed values of a line and everything worked out from them, in the unit the line has.</summary>
    public static void Fill(PurchaseReceiptLine line, decimal? quantity, decimal? unitPrice, decimal discount)
    {
        line.EnteredQuantity = quantity;
        line.EnteredUnitPrice = unitPrice;
        line.EnteredDiscount = discount;
        line.Quantity = quantity is { } q ? q * line.UnitFactor : null;
        line.UnitPrice = unitPrice is { } p ? PricePerBase(p, line.UnitFactor) : null;
        line.Discount = PricePerBase(discount, line.UnitFactor);
        line.LineAmount = PurchaseMoney.LineAmount(quantity, unitPrice, discount);
    }
}

