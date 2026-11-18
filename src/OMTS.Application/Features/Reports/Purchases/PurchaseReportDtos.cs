namespace OMTS.Application.Features.Reports.Purchases;

/// <summary>The groupings of the purchases report.</summary>
public enum PurchaseReportGrouping
{
    Supplier,
    Item,
}

/// <summary>
/// The query of the three calls (report, documents of a group, export). Dates are Vietnam dates, the current month when none is given. <c>GroupBy</c> is supplier (default) or item;
/// <c>SupplierIds</c> is repeated; <c>Kind</c> is Seed, Supply or all (default). <c>GroupKey</c> is the <c>key</c> of a group of the report, for the documents call only.
/// </summary>
public sealed record PurchaseReportQuery(
    DateOnly? From = null, DateOnly? To = null, string? GroupBy = null, Guid[]? SupplierIds = null, string? Kind = null, string? GroupKey = null);

/// <summary>The choices that were checked and applied; <c>Kind</c> is "all", "Seed" or "Supply".</summary>
public sealed record PurchaseReportChoices(IReadOnlyList<ReportChoice> Suppliers, string Kind);

/// <summary>
/// The figures of a group or of the total. A receipt is worth its lines less its discount; a return its lines less its discount. <c>NetValue</c> = <c>ReceivedValue − ReturnedValue</c>. The quantities
/// (BASE units of the item) are filled when grouped by item only. Grouped by item, the discounts of whole documents are the group <c>receipt-discounts</c> (negative), so the groups add up to the total.
/// </summary>
public sealed record PurchaseReportFigures(
    int ReceiptCount, int ReturnCount, decimal ReceivedValue, decimal ReturnedValue, decimal NetValue, decimal? ReceivedQuantity, decimal? ReturnedQuantity);

/// <param name="Key">The supplier id, the item id, or <c>receipt-discounts</c>.</param>
/// <param name="Code">The item code; null for a supplier and for the discounts group.</param>
/// <param name="Unit">The base unit name of the item; null for a supplier.</param>
public sealed record PurchaseReportGroup(
    string Key, string Label, string? Code, string? Unit,
    int ReceiptCount, int ReturnCount, decimal ReceivedValue, decimal ReturnedValue, decimal NetValue, decimal? ReceivedQuantity, decimal? ReturnedQuantity);

/// <summary>The report worked out when it is shown; <see cref="Message"/> is MSG-GEN-10 when nothing was bought or returned.</summary>
public sealed record PurchaseReport(
    DateTimeOffset ComputedAt, DateOnly From, DateOnly To, string GroupBy, PurchaseReportChoices Filters, IReadOnlyList<PurchaseReportGroup> Groups,
    PurchaseReportFigures Totals, string? Message, string? MessageCode);

public sealed record PurchaseDocumentSupplier(Guid Id, string Name);

/// <summary><c>Value</c> is positive; <c>IsReturn</c> says which way it counts. <c>ReceiptKind</c> is Seed or Supply for a receipt, null for a return.</summary>
public sealed record PurchaseDocumentRow(Guid Id, string Code, DateOnly Date, bool IsReturn, string? ReceiptKind, PurchaseDocumentSupplier Supplier, decimal Value);

/// <summary>The receipts and returns of one group, newest first; <see cref="Message"/> is MSG-GEN-10 when the group has none.</summary>
public sealed record PurchaseDocumentList(
    DateOnly From, DateOnly To, string GroupBy, string GroupKey, IReadOnlyList<PurchaseDocumentRow> Documents, string? Message, string? MessageCode);
