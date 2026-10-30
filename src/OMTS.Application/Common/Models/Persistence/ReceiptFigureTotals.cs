
namespace OMTS.Application.Common.Models.Persistence;

/// <summary>Counts of every matching receipt; the money sums are over the Completed ones only.</summary>
public sealed record ReceiptFigureTotals(int Count, int CompletedCount, decimal Subtotal, decimal Discount, decimal Returned, decimal Paid, decimal Refunded)
{
    public decimal Payable => Subtotal - Discount;

    public decimal Outstanding => Payable - Returned - (Paid - Refunded);

    public static ReceiptFigureTotals Empty { get; } = new(0, 0, 0, 0, 0, 0, 0);
}
