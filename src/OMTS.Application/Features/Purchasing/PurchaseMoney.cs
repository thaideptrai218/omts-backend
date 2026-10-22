using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Purchasing;

/// <summary>Money figures of one purchase receipt, all in whole VND (INP-03…INP-07).</summary>
/// <param name="Payable">Subtotal − discount.</param>
/// <param name="Returned">Σ return values of its purchase returns.</param>
/// <param name="Paid">Σ Recorded payments allocated to the receipt.</param>
/// <param name="Refunded">Σ refunds received on its returns.</param>
/// <param name="Outstanding">Net payable (payable − returned) − net paid (paid − refunded).</param>
public sealed record ReceiptFigures(decimal Subtotal, decimal Discount, decimal Payable, decimal Returned, decimal Paid, decimal Refunded, decimal Outstanding)
{
    public decimal NetPayable => Payable - Returned;

    public decimal NetPaid => Paid - Refunded;
}

/// <summary>The purchasing money rules in one place, as pure functions so they are unit tested.</summary>
public static class PurchaseMoney
{
    public const decimal Thousand = 1000m;

    public static decimal RoundToThousand(decimal amount) => Math.Round(amount / Thousand, MidpointRounding.AwayFromZero) * Thousand;

    public static bool IsThousands(decimal amount) => amount % Thousand == 0;

    /// <summary>Quantity × unit price, rounded to the nearest 1,000 VND.</summary>
    public static decimal LineAmount(decimal quantity, decimal unitPrice) => RoundToThousand(quantity * unitPrice);

    /// <summary>Value of returned goods: quantity × unit price × (payable ÷ subtotal), rounded to the nearest 1,000 VND.</summary>
    public static decimal ReturnValue(decimal quantity, decimal unitPrice, decimal payable, decimal subtotal) =>
        subtotal == 0 ? 0 : RoundToThousand(quantity * unitPrice * payable / subtotal);

    public static ReceiptFigures Of(PurchaseReceipt receipt)
    {
        var subtotal = receipt.Lines.Sum(l => l.LineAmount);
        var payable = subtotal - receipt.DiscountAmount;
        var returned = receipt.Returns.SelectMany(r => r.Lines).Sum(l => l.ReturnValue);
        var paid = receipt.Payments.Where(p => p.StatusId == StatusIds.SupplierPayment.Recorded).Sum(p => p.Amount);
        var refunded = receipt.Returns.Sum(r => r.RefundAmount);
        return new ReceiptFigures(subtotal, receipt.DiscountAmount, payable, returned, paid, refunded, payable - returned - (paid - refunded));
    }

    /// <summary>
    /// Raft material cost of a receipt: its RaftMaterial line amounts less their proportional share of the
    /// discount and less the values returned from those lines (FARM-04, INP-07). Only Completed receipts count.
    /// </summary>
    public static decimal RaftMaterialCost(PurchaseReceipt receipt)
    {
        if (receipt.StatusId != StatusIds.PurchaseReceipt.Completed)
        {
            return 0;
        }

        var subtotal = receipt.Lines.Sum(l => l.LineAmount);
        if (subtotal == 0)
        {
            return 0;
        }

        var payable = subtotal - receipt.DiscountAmount;
        var materialLines = receipt.Lines.Where(l => l.Item.Group == InputItemGroup.RaftMaterial).Select(l => l.Id).ToHashSet();
        var gross = receipt.Lines.Where(l => materialLines.Contains(l.Id)).Sum(l => l.LineAmount) * payable / subtotal;
        var returned = receipt.Returns.SelectMany(r => r.Lines).Where(l => materialLines.Contains(l.ReceiptLineId)).Sum(l => l.ReturnValue);
        return RoundToThousand(gross) - returned;
    }
}
