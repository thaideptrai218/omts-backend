using OMTS.Application.Features.AccessManagement.Dtos;
using OMTS.Application.Features.Purchasing.Receipts;
using OMTS.Application.Features.WaterQuality.Dtos;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Purchasing.Payments;

/// <param name="ConfirmAdvance">The Accountant answered Yes to MSG-PURCHASE-05: the payment is larger than the debt.</param>
/// <param name="ConfirmDuplicateBankReference">The Accountant answered Yes to MSG-PURCHASE-06: the bank reference is already recorded.</param>
/// <param name="DeduplicationKey">A repeated key returns the first payment and creates nothing.</param>
public sealed record RecordSupplierPaymentRequest(
    Guid? SupplierId,
    SupplierPaymentKind? Kind,
    DateOnly? PaymentDate,
    decimal? Amount,
    PaymentMethod? Method,
    string? BankReference = null,
    string? Note = null,
    bool ConfirmAdvance = false,
    bool ConfirmDuplicateBankReference = false,
    string? DeduplicationKey = null);

public sealed record CancelSupplierPaymentRequest(string? Reason, string? RowVersion = null);

/// <param name="DebtAfter">On a write: the supplier debt after it (on a repeated deduplication key: the debt now). Null on the reads.</param>
public sealed record SupplierPaymentView(
    Guid Id, string Code, SupplierRef Supplier, SupplierPaymentKind Kind, decimal Amount, DateOnly PaymentDate, PaymentMethod Method,
    string? BankReference, string? Note, int StatusId, string Status, DateTimeOffset? CancelledAt, string? CancelReason,
    AccountRef RecordedBy, string RowVersion, decimal? DebtAfter = null);

public sealed record SupplierPaymentQuery(
    Guid? SupplierId = null, SupplierPaymentKind? Kind = null, PaymentMethod? Method = null, int? StatusId = null,
    DateOnly? From = null, DateOnly? To = null, int Page = 1, int PageSize = 20);

/// <param name="TotalPaid">Σ Recorded payments of kind Payment of every match, not only of the page.</param>
/// <param name="TotalPaidBack">Σ Recorded payments of kind PaidBack, same scope.</param>
public sealed record SupplierPaymentPage(Paged<SupplierPaymentView> Page, decimal TotalPaid, decimal TotalPaidBack);
