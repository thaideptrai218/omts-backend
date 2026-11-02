using OMTS.Application.Features.Employees;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Purchasing.Receipts.Dtos;

public sealed record CompleteReceiptRequest(string? RowVersion);
