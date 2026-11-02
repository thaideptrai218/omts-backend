using OMTS.Application.Features.Employees;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Purchasing.Receipts.Dtos;

public sealed record ReceiptPaymentView(Guid Id, decimal Amount, DateOnly PaymentDate, PaymentMethod Method, string? ReferenceCode, int StatusId, string Status);
