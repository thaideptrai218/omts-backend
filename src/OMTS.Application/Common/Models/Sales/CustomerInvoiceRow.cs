using OMTS.Domain.Enums;

namespace OMTS.Application.Common.Models.Sales;

/// <summary>One invoice of a customer profile, in the figures the trader's list shows.</summary>
public sealed record CustomerInvoiceRow(
    Guid Id, string Code, DateTimeOffset InvoicedAt, DateOnly InvoiceDate, string ZoneName, string? PurchaseRequestCode, decimal TotalKg, decimal Total, int StatusId,
    SalesPaymentTerm PaymentTerm);
