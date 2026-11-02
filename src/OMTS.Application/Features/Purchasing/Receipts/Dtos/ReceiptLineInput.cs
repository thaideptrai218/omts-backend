using OMTS.Application.Features.Employees;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Purchasing.Receipts.Dtos;

public sealed record ReceiptLineInput(Guid? ItemId, decimal? Quantity, decimal? UnitPrice, string? SupplierLotCode = null, string? ActualSource = null);
