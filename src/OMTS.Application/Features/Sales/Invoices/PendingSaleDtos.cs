using OMTS.Application.Features.WaterQuality.Dtos;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Sales.Invoices;

/// <summary>
/// The VietQR code of a sale (BR-SALE-07 (1)): the client draws <paramref name="Payload"/> (EMVCo text) as the QR image; the other fields are shown next to it. Made again each time,
/// for the receiving bank account set at that time (BR-DEBT-01).
/// </summary>
/// <param name="Content">The transfer content: the invoice code.</param>
public sealed record VietQrView(string Payload, string BankBin, string BankName, string AccountNumber, string AccountHolderName, decimal Amount, string Content);

/// <summary>
/// A sale awaiting the transfer of its VietQR code (SALE-05 steps 7-8), as the port polls it.
/// </summary>
/// <param name="Status">Awaiting, Completed (the transfer arrived), PaymentChanged or Stopped.</param>
/// <param name="CashAmount">The cash part collected at the port (SALE-05.7), 0 for none.</param>
/// <param name="ToPayAmount">The amount of the VietQR code: the total less the cash part.</param>
/// <param name="ReceivedAmount">Transfers with this code that were too small to complete the sale (recorded with no customer, GFR-08); 0 normally.</param>
/// <param name="VietQr">While Awaiting; null after, or when the receiving bank account is no longer set up.</param>
/// <param name="Invoice">Once the invoice is saved (Completed or PaymentChanged): the invoice and MSG-SALE-06.</param>
public sealed record PendingSaleView(
    string Code, PendingSaleStatus Status, SalesInvoiceZoneView Zone, SalesInvoiceCustomerView? Customer, bool WalkInCustomer, decimal Total, decimal CashAmount, decimal ToPayAmount,
    decimal ReceivedAmount, VietQrView? VietQr, DateTimeOffset CreatedAt, AccountRef CreatedBy, Guid? InvoiceId, SalesInvoiceSaved? Invoice, DateTimeOffset? EndedAt);

/// <summary>SALE-05.0.E4 step 2: Cash, part in cash with Pay later, or Pay later; the rest of the sale is kept and the invoice keeps the code.</summary>
public sealed record ChangePendingSalePaymentRequest(SalesInvoicePaymentEntry? Payment);

/// <summary>SALE-05.0.E4 step 3: <paramref name="Confirmed"/> is the answer Yes to MSG-SALE-14; without it the question is asked (409).</summary>
public sealed record StopPendingSaleRequest(bool Confirmed = false);
