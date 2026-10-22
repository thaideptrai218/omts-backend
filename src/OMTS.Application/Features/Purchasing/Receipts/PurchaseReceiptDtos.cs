using OMTS.Application.Features.Employees;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Purchasing.Receipts;

public sealed record ReceiptLineInput(Guid? ItemId, decimal? Quantity, decimal? UnitPrice, string? SupplierLotCode = null, string? ActualSource = null);

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

public sealed record CompleteReceiptRequest(string? RowVersion);

/// <param name="CancelPayments">Required for a Completed receipt with Recorded payments: cancel them, or keep them as an unallocated supplier amount.</param>
public sealed record CancelReceiptRequest(string? Reason, bool? CancelPayments, string? RowVersion);

public sealed record SupplierRef(Guid Id, string Name, string PhoneNumber);

public sealed record RaftRef(Guid Id, string Code);

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

public sealed record ReceiptPaymentView(Guid Id, decimal Amount, DateOnly PaymentDate, PaymentMethod Method, string? ReferenceCode, int StatusId, string Status);

public sealed record ReceiptReturnView(Guid Id, string Code, DateOnly ReturnDate, string Reason, decimal ReturnValue, decimal RefundAmount, decimal DebtReductionAmount);

public sealed record PurchaseReceiptView(
    Guid Id,
    string Code,
    SupplierRef Supplier,
    ZoneRef Zone,
    RaftRef? Raft,
    DateOnly? ReceiptDate,
    decimal Subtotal,
    decimal DiscountAmount,
    decimal Payable,
    decimal Returned,
    decimal Paid,
    decimal Refunded,
    decimal Outstanding,
    decimal AmountPaidNow,
    PaymentMethod? PaidNowMethod,
    string? Notes,
    IReadOnlyList<string> InvoicePhotoUrls,
    int StatusId,
    string Status,
    DateTimeOffset? CompletedAt,
    DateTimeOffset? CancelledAt,
    string? CancelReason,
    Guid? CopiedFromReceiptId,
    IReadOnlyList<ReceiptLineView> Lines,
    IReadOnlyList<ReceiptPaymentView> Payments,
    IReadOnlyList<ReceiptReturnView> Returns,
    string RowVersion);
