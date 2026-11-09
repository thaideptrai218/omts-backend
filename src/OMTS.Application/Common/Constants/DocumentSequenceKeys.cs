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

    /// <summary>Sales invoices, HD000123 (BR-SALE-08).</summary>
    public const string SalesInvoice = "HD";

    /// <summary>Customer payments and money paid back, PT000123 (BR-DEBT-03 gives no prefix: PM default).</summary>
    public const string CustomerPayment = "PT";

    /// <summary>Quality claims, KN000123 (BR-CLAIM-01 gives no prefix: PM default).</summary>
    public const string QualityClaim = "KN";
}
