using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Sales.Invoices.Dtos;

/// <summary>One payment of the invoice as the trader reads it, without who collected or confirmed it (BR-DEBT-04).</summary>
public sealed record MyInvoicePaymentView(string Code, CustomerPaymentKind Kind, CustomerPaymentMethod Method, decimal Amount, DateOnly PaymentDate, int StatusId, string Status);
