using OMTS.Domain.Enums;

namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>One receipt with its money figures summed in the database, so lists and totals never load the receipt graph.</summary>
/// <param name="LinesTotal">Σ of the lines that match the line filters of the list (item group, item, raft); the total without them.</param>
/// <param name="Returned">Σ of its Recorded returns, each less its discount and with the VAT given back.</param>
/// <param name="Vat">Σ input VAT of its lines.</param>
public sealed record ReceiptFigureRow(
    Guid Id, string Code, DateOnly ReceiptDate, Guid SupplierId, string SupplierName, string SupplierPhone, int StatusId,
    Guid RecordedByAccountId, decimal Total, decimal LinesTotal, string SupplierCode, int ItemCount, decimal Returned, decimal Discount,
    Guid? ZoneId, string? ZoneCode, string? ZoneName, string? Notes, DateTimeOffset? CompletedAt, DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt,
    DateTimeOffset ReceivedAt, Guid? ReceivedByAccountId, IReadOnlyList<string> ReturnCodes, PurchaseReceiptKind Kind = PurchaseReceiptKind.Supply, decimal Vat = 0)
{
    /// <summary>What the receipt costs before returns: its total less its discount, plus its VAT.</summary>
    public decimal Payable => Total - Discount + Vat;
}

/// <summary>A purchase return with figures computed in SQL. <see cref="Value"/> is Σ line amounts as recorded and <see cref="Vat"/> Σ the VAT given back; what it lowers the debt by is <see cref="NetValue"/>.</summary>
public sealed record PurchaseReturnFigureRow(
    Guid Id, string Code, DateOnly ReturnDate, Guid? ReceiptId, string? ReceiptCode, Guid SupplierId, string SupplierCode, string SupplierName, string SupplierPhone,
    Guid? ZoneId, string? ZoneCode, string? ZoneName, string Reason, int StatusId, Guid RecordedByAccountId, decimal Value, decimal Discount, int LineCount, decimal Refunded,
    DateTimeOffset? CancelledAt, string? CancelReason, DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt, byte[] RowVersion, DateTimeOffset ReturnedAt,
    PurchaseReceiptKind Kind = PurchaseReceiptKind.Supply, decimal Vat = 0)
{
    public decimal NetValue => Value - Discount + Vat;
}

/// <summary>
/// PURCHASE-03 step 3: <see cref="Count"/> is every matching receipt; the money counts Completed receipts only (BR-PURCHASE-03),
/// the lines that match the line filters, and the returns dated in the period whatever the date received.
/// </summary>
public sealed record ReceiptListTotals(int Count, int CompletedCount, decimal ReceiptsTotal, decimal ReturnsTotal, decimal ReceiptsVat = 0, decimal ReturnsVat = 0)
{
    /// <summary>What the receipts leave owed after the returns: the goods and the VAT of both.</summary>
    public decimal NetTotal => ReceiptsTotal + ReceiptsVat - ReturnsTotal - ReturnsVat;

    public static ReceiptListTotals Empty { get; } = new(0, 0, 0, 0);
}
