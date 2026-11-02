using OMTS.Application.Features.Employees.Dtos;
using OMTS.Application.Features.Employees;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Purchasing.Receipts.Dtos;

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
