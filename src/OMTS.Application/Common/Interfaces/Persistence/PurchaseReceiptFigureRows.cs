namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>
/// One receipt with its money figures summed in the database (lines, returns, payments), so lists and totals never load
/// the receipt graph. Same rules as PurchaseMoney.Of: outstanding = total − returned − (paid − refunded).
/// </summary>
/// <param name="LinesTotal">Σ of the lines that match the line filters of the list (item group, item, raft); the total without them.</param>
public sealed record ReceiptFigureRow(
    Guid Id, string Code, DateOnly ReceiptDate, Guid SupplierId, string SupplierName, string SupplierPhone, int StatusId,
    Guid RecordedByAccountId, decimal Total, decimal LinesTotal, decimal Returned, decimal Paid, decimal Refunded)
{
    public decimal Outstanding => Total - Returned - (Paid - Refunded);
}

/// <summary>
/// PURCHASE-03 step 3: <see cref="Count"/> is every matching receipt; the money counts Completed receipts only (BR-PURCHASE-03),
/// the lines that match the line filters, and the returns dated in the period whatever the date received.
/// </summary>
/// <param name="RaftMaterialCost">With a raft filter: its material cost in the period, Material lines only (BR-FARM-05).</param>
public sealed record ReceiptListTotals(int Count, int CompletedCount, decimal ReceiptsTotal, decimal ReturnsTotal, decimal? RaftMaterialCost)
{
    public decimal NetTotal => ReceiptsTotal - ReturnsTotal;

    public static ReceiptListTotals Empty { get; } = new(0, 0, 0, 0, null);
}

/// <summary>Σ of the Completed receipts of one supplier, aggregated in the database.</summary>
public sealed record SupplierDebtFigures(Guid SupplierId, string SupplierName, decimal Total, decimal Returned, decimal Paid, decimal Refunded)
{
    public decimal Outstanding => Total - Returned - (Paid - Refunded);
}

/// <summary>A Material line of a Completed receipt that names a raft (BR-FARM-05), received in the period asked for.</summary>
public sealed record RaftCostLineRow(
    Guid RaftId, string RaftCode, Guid ZoneId, string ZoneCode, string ZoneName,
    Guid ReceiptId, string ReceiptCode, DateOnly ReceiptDate, Guid SupplierId, string SupplierName,
    Guid LineId, int LineNumber, Guid ItemId, string ItemCode, string ItemName, string Unit, decimal Quantity, decimal UnitPrice, decimal LineAmount);

/// <summary>A returned quantity of such a line, dated in the period asked for (BR-FARM-05: a return counts by its return date).</summary>
public sealed record RaftCostReturnRow(
    Guid RaftId, string RaftCode, Guid ZoneId, string ZoneCode, string ZoneName,
    Guid ReturnId, string ReturnCode, DateOnly ReturnDate, Guid ReceiptId, string ReceiptCode,
    Guid LineId, int LineNumber, string ItemName, string Unit, decimal Quantity, decimal ReturnValue);
