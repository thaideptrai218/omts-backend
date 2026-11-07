using System.Globalization;
using OMTS.Application.Common.Constants;

namespace OMTS.Application.Features.Sales.Invoices;

/// <summary>The codes of the sales documents, each from its own document sequence, so a code is never given twice: HD000123 (BR-SALE-08) and PT000123 for customer payments.</summary>
public static class SalesDocumentCodes
{
    /// <summary>HD followed by at least six digits: HD000001…HD999999, then HD1000000.</summary>
    public static string InvoiceCode(long number) => Code(DocumentSequenceKeys.SalesInvoice, number);

    /// <summary>PT followed by at least six digits: the "receipt" of a customer payment (BR-DEBT-03).</summary>
    public static string PaymentCode(long number) => Code(DocumentSequenceKeys.CustomerPayment, number);

    private static string Code(string prefix, long number) => $"{prefix}{number.ToString("000000", CultureInfo.InvariantCulture)}";
}
