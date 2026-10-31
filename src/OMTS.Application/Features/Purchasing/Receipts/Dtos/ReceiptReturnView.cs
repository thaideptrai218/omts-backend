using OMTS.Application.Features.Employees;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Purchasing.Receipts.Dtos;

public sealed record ReceiptReturnView(Guid Id, string Code, DateOnly ReturnDate, string Reason, decimal ReturnValue, decimal RefundAmount, decimal DebtReductionAmount);
