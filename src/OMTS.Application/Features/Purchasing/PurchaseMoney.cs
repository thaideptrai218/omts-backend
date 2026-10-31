using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Purchasing;

namespace OMTS.Application.Features.Purchasing;

/// <summary>Money figures of one purchase receipt in VND (PURCHASE-01…03; payments until PURCHASE-05 unties them).</summary>
/// <param name="Total">Σ line amounts (BR-PURCHASE-02).</param>
/// <param name="Returned">Σ return values of its purchase returns.</param>
/// <param name="Paid">Σ Recorded payments still allocated to the receipt.</param>
/// <param name="Refunded">Σ refunds received on its returns.</param>
/// <param name="Outstanding">Net total (total − returned) − net paid (paid − refunded).</param>
public sealed record ReceiptFigures(decimal Total, decimal Returned, decimal Paid, decimal Refunded, decimal Outstanding)
{
    public decimal NetTotal => Total - Returned;

    public decimal NetPaid => Paid - Refunded;
}

/// <summary>The purchasing money rules in one place, as pure functions so they are unit tested.</summary>
public static class PurchaseMoney
{
    public const decimal Thousand = 1000m;

    public static decimal RoundToThousand(decimal amount) => Math.Round(amount / Thousand, MidpointRounding.AwayFromZero) * Thousand;

    /// <summary>Supplier payments and the money of purchase returns are still whole thousands until PURCHASE-02/05 change them.</summary>
    public static bool IsThousands(decimal amount) => amount % Thousand == 0;

    /// <summary>
    /// Quantity × unit price, rounded to the dong only (BR-PURCHASE-02: amounts are not rounded; VND has no smaller unit,
    /// so 1.5 × 333 = 499.5 is 500).
    /// </summary>
    public static decimal LineAmount(decimal quantity, decimal unitPrice) => Math.Round(quantity * unitPrice, 0, MidpointRounding.AwayFromZero);

    /// <summary>The amount of a line, 0 while a Draft line has no quantity or no unit price yet.</summary>
    public static decimal LineAmount(decimal? quantity, decimal? unitPrice) =>
        quantity is { } q && unitPrice is { } p ? LineAmount(q, p) : 0;

    /// <summary>The total of a receipt: the sum of its line amounts.</summary>
    public static decimal Total(IEnumerable<PurchaseReceiptLine> lines) => lines.Sum(l => l.LineAmount);

    /// <summary>Value of returned goods: quantity × unit price, rounded to the nearest 1,000 VND (the return rework, P4, makes it exact).</summary>
    public static decimal ReturnValue(decimal quantity, decimal unitPrice) => RoundToThousand(quantity * unitPrice);

    /// <summary>
    /// Return value capped by what is left of the line's rounded value, so returning a line in parts never credits more
    /// than the line is worth and its last unit can still be returned.
    /// </summary>
    public static decimal CappedReturnValue(decimal quantity, PurchaseReceiptLine line, decimal alreadyReturned) =>
        Math.Min(ReturnValue(quantity, line.UnitPrice ?? 0), Math.Max(0, RoundToThousand(line.LineAmount) - alreadyReturned));

    /// <summary>
    /// Receipt-level cap of one return: the values never add up to more than what is left of the total (total − already
    /// returned, in whole thousands), and the return that leaves nothing returnable is worth exactly that rest, so the
    /// rounding of the per-line values never blocks the last return. The difference goes on the last lines.
    /// </summary>
    public static decimal[] FitToReceipt(IReadOnlyList<decimal> values, decimal remaining, bool leavesNothingReturnable)
    {
        var fitted = values.ToArray();
        remaining = Math.Max(0, remaining);
        var difference = (leavesNothingReturnable ? remaining : Math.Min(fitted.Sum(), remaining)) - fitted.Sum();
        for (var i = fitted.Length - 1; i >= 0 && difference != 0; i--)
        {
            var adjusted = Math.Max(0, fitted[i] + difference);
            difference -= adjusted - fitted[i];
            fitted[i] = adjusted;
        }

        return fitted;
    }

    public static ReceiptFigures Of(PurchaseReceipt receipt)
    {
        var total = Total(receipt.Lines);
        var returned = receipt.Returns.SelectMany(r => r.Lines).Sum(l => l.ReturnValue);
        var paid = receipt.Payments.Where(p => p.StatusId == StatusIds.SupplierPayment.Recorded).Sum(p => p.Amount);
        var refunded = receipt.Returns.Sum(r => r.RefundAmount);
        return new ReceiptFigures(total, returned, paid, refunded, total - returned - (paid - refunded));
    }
}
