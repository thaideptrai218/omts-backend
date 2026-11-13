using OMTS.Application.Features.Purchasing;

namespace OMTS.Application.Features.Sales.Invoices;

/// <summary>How the cash given at the port is split (BR-SALE-07 (3) to (5)).</summary>
/// <param name="Recorded">The cash payment recorded with the invoice: the part of the total it pays plus the part subtracted from the debt; 0 records none.</param>
/// <param name="SubtractedFromDebt">The cash above the total that lowers the customer debt, never more than the debt before the invoice.</param>
/// <param name="Change">The cash above the total given back to the trader.</param>
/// <param name="Rest">The part of the total the cash does not pay: Pay later (or VietQR, PR 7).</param>
public sealed record PortCashSplit(decimal Recorded, decimal SubtractedFromDebt, decimal Change, decimal Rest)
{
    /// <summary>The cash above the total: change, the part subtracted from the debt, or both.</summary>
    public decimal Extra => SubtractedFromDebt + Change;
}

/// <summary>The money rules of a sales invoice in one place, as pure functions so they are unit tested. Amounts are whole VND, kg have at most 3 decimals.</summary>
public static class InvoiceMoney
{
    /// <summary>
    /// Kg × unit price, rounded half up to the dong (the plan's rule for every line amount, shared with BR-PURCHASE-02): 200 kg × 30,000 is 6,000,000 (AC-06), 12.345 kg × 30,001 =
    /// 370,362.345 is 370,362. "Not rounded" in BR-SALE-06 means no rounding to thousands; a payment cannot hold a part of a dong.
    /// </summary>
    public static decimal LineAmount(decimal kg, decimal unitPrice) => PurchaseMoney.RoundHalfUp(kg * unitPrice);

    /// <summary>The amount of a line when it fits its column; false when kg × price is too large for an amount of 18 digits, or for a decimal at all (MSG-GEN-01, not a 500).</summary>
    public static bool TryLineAmount(decimal kg, decimal unitPrice, out decimal amount)
    {
        try
        {
            amount = LineAmount(kg, unitPrice);
        }
        catch (OverflowException)
        {
            amount = 0m;
            return false;
        }

        return IsWholeAmount(amount);
    }

    /// <summary>The sum of the rounded line amounts, not the rounded sum of the products.</summary>
    public static decimal Subtotal(IEnumerable<decimal> lineAmounts) => lineAmounts.Sum();

    /// <summary>BR-SALE-06: the line amounts, minus the discount, plus the other charges, minus the price cuts. The caller refuses a total below 0.</summary>
    public static decimal Total(decimal subtotal, decimal discount, decimal otherCharges, decimal priceCuts = 0m) => subtotal - discount + otherCharges - priceCuts;

    /// <summary>
    /// BR-SALE-07: cash up to the total pays the invoice and the rest is paid another way. Cash above the total is change, or at the trader's wish lowers the customer debt, but only up to
    /// the debt before the invoice, so cash at the port never creates money owed back. A walk-in customer has no debt: pass 0.
    /// </summary>
    public static PortCashSplit SplitCash(decimal total, decimal cash, decimal debtBefore, bool subtractExtraFromDebt)
    {
        var paysOfTotal = Math.Min(cash, total);
        var extra = Math.Max(0m, cash - total);
        var subtracted = subtractExtraFromDebt ? Math.Min(extra, Math.Max(0m, debtBefore)) : 0m;
        return new PortCashSplit(paysOfTotal + subtracted, subtracted, extra - subtracted, total - paysOfTotal);
    }

    /// <summary>MSG-SALE-11 is asked only when there is cash above the total and a debt to lower (SALE-05.6 step 3).</summary>
    public static bool ExtraCanLowerDebt(decimal total, decimal cash, decimal debtBefore) => cash > total && debtBefore > 0m;

    /// <summary>Whole VND, 0 or more: a dong cannot be split, and an amount of 18 digits fits the columns.</summary>
    public static bool IsWholeAmount(decimal? amount) => amount is >= 0m and <= MaxAmount && decimal.Truncate(amount.Value) == amount.Value;

    /// <summary>The worked-out amounts of an invoice (never below 0 here) fit their decimal(18,0) columns: amounts that fit one by one can overflow together.</summary>
    public static bool TotalsFit(decimal subtotal, decimal total, decimal priceCuts = 0m) => IsWholeAmount(subtotal) && IsWholeAmount(total) && IsWholeAmount(priceCuts);

    /// <summary>Above 0 and at most 3 decimals: more would be rounded by the column, possibly to 0.</summary>
    public static bool IsKg(decimal? kg) => kg is > 0m and <= MaxKg && decimal.Round(kg.Value, 3) == kg.Value;

    // decimal(18,0) and decimal(18,3).
    private const decimal MaxAmount = 999_999_999_999_999_999m;
    private const decimal MaxKg = 999_999_999_999_999.999m;
}
