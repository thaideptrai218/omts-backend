using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Purchasing;

namespace OMTS.Application.Features.Purchasing;

/// <summary>Money figures of one purchase receipt in VND (PURCHASE-01…03); what payments cover of it is the ledger's reading (BR-PURCHASE-04).</summary>
/// <param name="Total">Σ rounded line amounts (BR-PURCHASE-02).</param>
/// <param name="Returned">Σ of its Recorded purchase returns as recorded, each less its own discount.</param>
/// <param name="Discount">The discount of the receipt.</param>
public sealed record ReceiptFigures(decimal Total, decimal Returned, decimal Discount = 0)
{
    /// <summary>What the receipt costs the cooperative before returns: its total less its discount.</summary>
    public decimal Payable => Total - Discount;

    /// <summary>What the receipt adds to the supplier debt: its total, less its discount and its returns.</summary>
    public decimal DebtShare => Payable - Returned;
}

/// <summary>The purchasing money rules in one place, as pure functions so they are unit tested.</summary>
public static class PurchaseMoney
{
    /// <summary>
    /// The rounding of purchasing money: half up to a whole number (BR-PURCHASE-02, PM 03/11), so 561,242.5 is 561,243 where
    /// the .NET default (half to even) gives 561,242. These amounts are never negative, and there "away from zero" is half
    /// up; MidpointRounding.ToPositiveInfinity is not, it is a ceiling.
    /// </summary>
    public static decimal RoundHalfUp(decimal amount) => Math.Round(amount, 0, MidpointRounding.AwayFromZero);

    /// <summary>Quantity × unit price, half up to the dong and never to thousands (BR-PURCHASE-02): 12.5 × 45,501 = 568,762.5 is 568,763.</summary>
    public static decimal LineAmount(decimal quantity, decimal unitPrice) => RoundHalfUp(quantity * unitPrice);

    /// <summary>The amount of a line, 0 while a Draft line has no quantity or no unit price yet.</summary>
    public static decimal LineAmount(decimal? quantity, decimal? unitPrice) =>
        quantity is { } q && unitPrice is { } p ? LineAmount(q, p) : 0;

    /// <summary>
    /// The amount of a receipt line with a discount per unit: quantity × (unit price − discount), half up to the dong, so
    /// 3 × (10,000 − 500) is 28,500. 0 while a Draft line has no quantity or no unit price yet.
    /// </summary>
    public static decimal LineAmount(decimal? quantity, decimal? unitPrice, decimal discount) =>
        quantity is { } q && unitPrice is { } p ? LineAmount(q, p - discount) : 0;

    /// <summary>What one unit of the line cost: its unit price less its discount per unit (0 while it has no price).</summary>
    public static decimal NetUnitPrice(PurchaseReceiptLine line) => (line.UnitPrice ?? 0) - line.Discount;

    /// <summary>
    /// What one unit of the line was paid, the price a return defaults to: <see cref="NetUnitPrice"/> less its share of the receipt
    /// discount, prorated by line amount, half up to the dong. 204 × 3,000 = 612,000 with 60,300 off the receipt is
    /// 3,000 × 551,700 / 612,000 = 2,704.4, so 2,704 a unit.
    /// </summary>
    public static decimal ReturnUnitPrice(PurchaseReceipt receipt, PurchaseReceiptLine line)
    {
        var total = Total(receipt.Lines);
        return total > 0 ? RoundHalfUp(NetUnitPrice(line) * (total - receipt.Discount) / total) : NetUnitPrice(line);
    }

    /// <summary>The total of a receipt: the sum of its rounded line amounts, not the rounded sum of the products (BR-PURCHASE-02).</summary>
    public static decimal Total(IEnumerable<PurchaseReceiptLine> lines) => lines.Sum(l => l.LineAmount);

    /// <summary>The returns of the receipt that count: a Draft or Cancelled return counts nowhere (BR-PURCHASE-07).</summary>
    public static IEnumerable<PurchaseReturn> RecordedReturns(PurchaseReceipt receipt) =>
        receipt.Returns.Where(r => r.StatusId == StatusIds.PurchaseReturn.Recorded);

    public static ReceiptFigures Of(PurchaseReceipt receipt) => new(
        Total(receipt.Lines),
        RecordedReturns(receipt).SelectMany(r => r.Lines).Sum(l => l.ReturnValue) - RecordedReturns(receipt).Sum(r => r.Discount),
        receipt.Discount);
}
