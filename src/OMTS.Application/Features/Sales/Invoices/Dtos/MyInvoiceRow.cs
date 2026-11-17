using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Sales.Invoices.Dtos;

/// <summary>One row of the trader's own invoices.</summary>
/// <param name="IsPaid">Null for a Cancelled invoice (<see cref="SalesInvoicePaidState"/>).</param>
/// <param name="UnpaidAmount">What is still to pay; 0 for a Cancelled invoice.</param>
public sealed record MyInvoiceRow(
    Guid Id, string Code, DateTimeOffset InvoicedAt, DateOnly InvoiceDate, string ZoneName, string? PurchaseRequestCode, decimal TotalKg, decimal Total, int StatusId,
    string Status, SalesPaymentTerm PaymentTerm, bool? IsPaid, decimal UnpaidAmount);
