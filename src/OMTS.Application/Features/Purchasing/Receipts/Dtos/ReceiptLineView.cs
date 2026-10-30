using OMTS.Application.Features.Employees;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Purchasing.Receipts.Dtos;

/// <param name="Returnable">Received − stocked − already returned (non-seed lines are never stocked).</param>
public sealed record ReceiptLineView(
    Guid Id,
    Guid ItemId,
    string ItemCode,
    string ItemName,
    InputItemGroup Group,
    string Unit,
    decimal Quantity,
    decimal UnitPrice,
    decimal LineAmount,
    string? SupplierLotCode,
    string? ActualSource,
    Guid? SeedLotId,
    string? SeedLotCode,
    decimal Stocked,
    decimal ReturnedQuantity,
    decimal Returnable);
