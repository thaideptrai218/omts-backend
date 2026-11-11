namespace OMTS.Application.Common.Constants;

/// <summary>
/// The keys of the document number sequences (<c>IDocumentNumberAllocator</c>), one per kind of document. SALE (HD) and wage
/// vouchers add theirs here when they move to the allocator.
/// </summary>
public static class DocumentSequenceKeys
{
    /// <summary>Suppliers, NCC000123.</summary>
    public const string Supplier = "NCC";

    /// <summary>Employees, NV000123 (LABOR-01).</summary>
    public const string Employee = "NV";

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

    /// <summary>Payroll sheets, BL000123 (LABOR-12).</summary>
    public const string PayrollSheet = "BL";

    /// <summary>Payslips, PL000123 (LABOR-12).</summary>
    public const string PayrollLine = "PL";

    /// <summary>Cash book receipt vouchers of the Cash fund, TTM000123.</summary>
    public const string CashReceiptCash = "TTM";

    /// <summary>Cash book payment vouchers of the Cash fund, CTM000123.</summary>
    public const string CashPaymentCash = "CTM";

    /// <summary>Cash book receipt vouchers of the Bank fund, TNH000123.</summary>
    public const string CashReceiptBank = "TNH";

    /// <summary>Cash book payment vouchers of the Bank fund, CNH000123.</summary>
    public const string CashPaymentBank = "CNH";

    /// <summary>Cash book receipt vouchers of the EWallet fund, TVD000123.</summary>
    public const string CashReceiptEWallet = "TVD";

    /// <summary>Cash book payment vouchers of the EWallet fund, CVD000123.</summary>
    public const string CashPaymentEWallet = "CVD";
}
