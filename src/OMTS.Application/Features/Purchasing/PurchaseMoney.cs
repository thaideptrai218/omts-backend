using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Purchasing;

namespace OMTS.Application.Features.Purchasing;

/// <summary>Money figures of one purchase receipt in VND (PURCHASE-01…03). Payments are not linked to receipts (BR-PURCHASE-04).</summary>
/// <param name="Total">Σ rounded line amounts (BR-PURCHASE-02).</param>
/// <param name="Returned">Σ amounts of its purchase returns that are not cancelled, as recorded.</param>
public sealed record ReceiptFigures(decimal Total, decimal Returned)
{
    /// <summary>What the receipt adds to the supplier debt: its total, less its returns.</summary>
    public decimal DebtShare => Total - Returned;
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

    /// <summary>The total of a receipt: the sum of its rounded line amounts, not the rounded sum of the products (BR-PURCHASE-02).</summary>
    public static decimal Total(IEnumerable<PurchaseReceiptLine> lines) => lines.Sum(l => l.LineAmount);

    /// <summary>The returns of the receipt that count: a Cancelled return counts nowhere (BR-PURCHASE-07).</summary>
    public static IEnumerable<PurchaseReturn> RecordedReturns(PurchaseReceipt receipt) =>
        receipt.Returns.Where(r => r.StatusId == StatusIds.PurchaseReturn.Recorded);

    public static ReceiptFigures Of(PurchaseReceipt receipt) => new(
        Total(receipt.Lines),
        RecordedReturns(receipt).SelectMany(r => r.Lines).Sum(l => l.ReturnValue));
}
