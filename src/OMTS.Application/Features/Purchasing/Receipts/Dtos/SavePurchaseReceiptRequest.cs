using OMTS.Application.Features.Employees;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Purchasing.Receipts.Dtos;

/// <summary>
/// INP-03 create / draft edit. <see cref="Complete"/> false saves a Draft (only supplier and zone needed);
/// true validates fully and completes it in the same call.
/// </summary>
public sealed record SavePurchaseReceiptRequest(
    Guid? SupplierId,
    Guid? ZoneId,
    Guid? RaftId,
    DateOnly? ReceiptDate,
    decimal? DiscountAmount,
    decimal? AmountPaidNow,
    PaymentMethod? PaidNowMethod,
    string? Notes,
    IReadOnlyList<ReceiptLineInput>? Lines,
    bool Complete = false,
    string? RowVersion = null);
