using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Sales.Invoices.Dtos;

/// <summary>The trader's own invoice: the weighing, the money and the QR code, without the staff data of <see cref="SalesInvoiceView"/> (sendings, approvals, commands).</summary>
/// <param name="CageCount">The cages weighed on the invoice; 0 when it was sold by the kg.</param>
/// <param name="IsPaid">Null for a Cancelled invoice (<see cref="SalesInvoicePaidState"/>).</param>
/// <param name="UnpaidAmount">What is still to pay; 0 for a Cancelled invoice.</param>
/// <param name="QrUrl">The provenance page of the invoice, null when it has no QR code.</param>
public sealed record MyInvoiceView(
    Guid Id, string Code, DateTimeOffset InvoicedAt, DateOnly InvoiceDate, string ZoneName, Guid? PurchaseRequestId, string? PurchaseRequestCode, IReadOnlyList<MyInvoiceLineView> Lines,
    int CageCount, decimal TotalKg, decimal Subtotal, decimal Discount, decimal OtherCharges, decimal PriceCutTotal, decimal Total, SalesPaymentTerm PaymentTerm, int StatusId, string Status,
    bool? IsPaid, decimal Covered, decimal UnpaidAmount, IReadOnlyList<MyInvoicePaymentView> Payments, string? QrUrl, Guid? PortWeighingId);
