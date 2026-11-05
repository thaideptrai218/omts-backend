namespace OMTS.Application.Common.Constants;

/// <summary>
/// The keys of the document number sequences (<c>IDocumentNumberAllocator</c>), one per kind of document. SALE (HD), supplier
/// payments and wage vouchers add theirs here when they move to the allocator.
/// </summary>
public static class DocumentSequenceKeys
{
    /// <summary>Purchase receipts, PN000123 (GBR-04).</summary>
    public const string PurchaseReceipt = "PN";
}
