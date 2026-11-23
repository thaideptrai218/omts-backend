using OMTS.Domain.Common;

namespace OMTS.Domain.Entities.Sales;

/// <summary>
/// One time a trader pays an invoice from the Zalo Mini App (decision of 21/11/2026): Zalo's Checkout SDK is required for a payment inside a Mini App, and payOS makes the
/// QR code and reports the transfer. The money itself is a <see cref="CustomerPayment"/> waiting for its transfer, of the amount left to pay on the invoice, so the debt, the
/// statement and the Owner's notice work as for any payment. This row keeps what only the Mini App needs: the payOS order and its QR code, the Zalo order the trader
/// confirmed and whether Zalo was told that the payment arrived. The web keeps its own VietQR flow and never makes one.
/// </summary>
public class TraderCheckout : AuditableEntity
{
    /// <summary>The payment waiting for the transfer; one checkout per payment.</summary>
    public Guid PaymentId { get; set; }
    public CustomerPayment Payment { get; set; } = null!;

    /// <summary>The invoice paid: the payment pays it only.</summary>
    public Guid InvoiceId { get; set; }
    public SalesInvoice Invoice { get; set; } = null!;

    /// <summary>The trader account that started it.</summary>
    public Guid AccountId { get; set; }

    /// <summary>The amount asked, whole VND: what was left to pay on the invoice when the checkout started.</summary>
    public decimal Amount { get; set; }

    /// <summary>The order code of the payOS payment link, unique: the payOS notification names the checkout by it.</summary>
    public long PayOsOrderCode { get; set; }

    public string PayOsPaymentLinkId { get; set; } = string.Empty;

    /// <summary>The VietQR text of the payOS link, which the Mini App draws (it never opens the payOS page).</summary>
    public string QrContent { get; set; } = string.Empty;

    public string BankBin { get; set; } = string.Empty;
    public string AccountNumber { get; set; } = string.Empty;
    public string AccountName { get; set; } = string.Empty;

    /// <summary>The transfer content payOS put in the QR code: the code of the payment.</summary>
    public string TransferContent { get; set; } = string.Empty;

    /// <summary>After this the payOS link no longer takes money and the trader starts a new checkout.</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>The order Zalo made when the trader confirmed the payment in Zalo (<c>PAY_BY_CUSTOM_METHOD</c>); Zalo is told about this order when the money arrives.</summary>
    public string? ZaloOrderId { get; set; }

    public DateTimeOffset? ZaloOrderLinkedAt { get; set; }

    /// <summary>When Zalo accepted the report that the payment arrived in full; null until then.</summary>
    public DateTimeOffset? ZaloReportedAt { get; set; }

    public int ZaloReportAttempts { get; set; }

    public DateTimeOffset? ZaloLastAttemptAt { get; set; }

    /// <summary>What Zalo (or the network) answered to the last report that failed; null once a report succeeded.</summary>
    public string? ZaloLastError { get; set; }
}
