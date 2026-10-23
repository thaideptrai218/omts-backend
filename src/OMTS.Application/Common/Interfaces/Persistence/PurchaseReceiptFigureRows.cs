namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>
/// One receipt with its money figures summed in the database (lines, returns, payments), so lists and totals never load
/// the receipt graph. Same rules as PurchaseMoney.Of: payable = subtotal − discount, outstanding = payable − returned − (paid − refunded).
/// </summary>
public sealed record ReceiptFigureRow(
    Guid Id, string Code, DateOnly ReceiptDate, Guid SupplierId, string SupplierName, string SupplierPhone,
    Guid ZoneId, string ZoneCode, string ZoneName, Guid? RaftId, string? RaftCode, int StatusId,
    decimal Subtotal, decimal Discount, decimal Returned, decimal Paid, decimal Refunded)
{
    public decimal Payable => Subtotal - Discount;

    public decimal Outstanding => Payable - Returned - (Paid - Refunded);
}

/// <summary>Counts of every matching receipt; the money sums are over the Completed ones only.</summary>
public sealed record ReceiptFigureTotals(int Count, int CompletedCount, decimal Subtotal, decimal Discount, decimal Returned, decimal Paid, decimal Refunded)
{
    public decimal Payable => Subtotal - Discount;

    public decimal Outstanding => Payable - Returned - (Paid - Refunded);

    public static ReceiptFigureTotals Empty { get; } = new(0, 0, 0, 0, 0, 0, 0);
}

/// <summary>Σ of the Completed receipts of one supplier, aggregated in the database.</summary>
public sealed record SupplierDebtFigures(Guid SupplierId, string SupplierName, decimal Payable, decimal Returned, decimal Paid, decimal Refunded)
{
    public decimal Outstanding => Payable - Returned - (Paid - Refunded);
}
