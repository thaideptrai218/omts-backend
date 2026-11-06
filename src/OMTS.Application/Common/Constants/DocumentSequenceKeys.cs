namespace OMTS.Application.Common.Constants;

/// <summary>
/// The keys of the document number sequences (<c>IDocumentNumberAllocator</c>), one per kind of document. SALE (HD) and wage
/// vouchers add theirs here when they move to the allocator.
/// </summary>
public static class DocumentSequenceKeys
{
    /// <summary>Purchase receipts, PN000123 (GBR-04).</summary>
    public const string PurchaseReceipt = "PN";

    /// <summary>Supplier payments and money paid back, TT000123 (BR-PURCHASE-08).</summary>
    public const string SupplierPayment = "TT";

    /// <summary>Purchase returns, TH000123 (PURCHASE-02).</summary>
    public const string PurchaseReturn = "TH";
}
