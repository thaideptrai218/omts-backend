using OMTS.Domain.Enums;

namespace OMTS.Application.Common.Models.Sales;

/// <summary>One invoice of a customer profile, in the figures the trader's list shows.</summary>
/// <param name="CageCount">The cages weighed on the invoice; 0 when it was sold by the kg.</param>
public sealed record CustomerInvoiceRow(
    Guid Id, string Code, DateTimeOffset InvoicedAt, DateOnly InvoiceDate, string ZoneName, string? PurchaseRequestCode, int CageCount, decimal TotalKg, decimal Total, int StatusId,
    SalesPaymentTerm PaymentTerm);
