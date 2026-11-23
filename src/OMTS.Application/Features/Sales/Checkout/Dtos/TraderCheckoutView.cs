using OMTS.Application.Common.Models.Payments;

namespace OMTS.Application.Features.Sales.Checkout.Dtos;

/// <summary>
/// A trader's Mini App checkout as the Mini App polls it. The client never decides that the money arrived: <see cref="PaidInFull"/> is the server's reading of the
/// payment (CLAUDE.md §10).
/// </summary>
/// <param name="PaymentStatusCode">The status of the payment in the catalog: PENDING_TRANSFER while it waits, COMPLETED once a transfer arrived, CANCELLED.</param>
/// <param name="ReceivedAmount">What arrived, once the payment is Completed; less than <paramref name="Amount"/> for a part payment.</param>
/// <param name="PaidInFull">The payment is Completed with at least the amount asked.</param>
/// <param name="Expired">The payment still waits but the payOS link no longer takes money: the trader starts a new checkout.</param>
/// <param name="Checkout">The signed <c>CheckoutSDK.createOrder</c> parameters, passed on unchanged.</param>
/// <param name="ZaloOrderLinked">The Zalo order of <c>PAY_BY_CUSTOM_METHOD</c> was sent.</param>
/// <param name="ZaloReported">Zalo accepted the report that the order was paid.</param>
public sealed record TraderCheckoutView(
    Guid Id,
    Guid InvoiceId,
    string InvoiceCode,
    string PaymentCode,
    decimal Amount,
    int PaymentStatusId,
    string PaymentStatusCode,
    string PaymentStatus,
    decimal? ReceivedAmount,
    bool PaidInFull,
    bool Expired,
    DateTimeOffset ExpiresAt,
    TraderCheckoutQr Qr,
    ZaloCheckoutOrder Checkout,
    bool ZaloOrderLinked,
    bool ZaloReported);
