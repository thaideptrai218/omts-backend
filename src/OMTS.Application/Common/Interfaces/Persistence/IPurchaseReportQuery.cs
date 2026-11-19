using OMTS.Domain.Enums;

namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>One item line of a purchase receipt or a purchase return: the quantity in BASE units of the item and the money of the line (whole VND).</summary>
public sealed record PurchaseReportLine(Guid ItemId, string ItemCode, string ItemName, string Unit, decimal Quantity, decimal Amount);

/// <summary>
/// A Completed purchase receipt (<paramref name="IsReturn"/> false) or a Recorded purchase return (true) of the period. <paramref name="Discount"/> is taken off the whole document, so the value
/// of the document is its lines' amounts less this. <paramref name="ReceiptKind"/> is Seed or Supply for a receipt, null for a return.
/// </summary>
public sealed record PurchaseReportDocument(
    Guid Id, string Code, DateOnly Date, bool IsReturn, Guid SupplierId, string SupplierName, PurchaseReceiptKind? ReceiptKind, decimal Discount, IReadOnlyList<PurchaseReportLine> Lines);

/// <summary>What the purchases report reads: receipts and returns of the period, with their lines. Read only; a fixed number of commands.</summary>
public interface IPurchaseReportQuery
{
    /// <param name="supplierIds">Only these suppliers when not empty.</param>
    /// <param name="kind">Seed: seed receipts and returns of seed items; Supply: the others; null: all.</param>
    Task<IReadOnlyList<PurchaseReportDocument>> DocumentsAsync(
        DateOnly from, DateOnly to, IReadOnlyCollection<Guid> supplierIds, PurchaseReceiptKind? kind, CancellationToken cancellationToken);

    /// <summary>The names of the suppliers that exist among <paramref name="ids"/>.</summary>
    Task<IReadOnlyDictionary<Guid, string>> SupplierNamesAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);
}
