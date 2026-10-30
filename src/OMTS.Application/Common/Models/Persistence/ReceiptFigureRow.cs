
namespace OMTS.Application.Common.Models.Persistence;

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
