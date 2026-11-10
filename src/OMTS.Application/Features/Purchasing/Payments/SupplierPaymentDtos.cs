using OMTS.Application.Features.AccessManagement.Dtos;
using OMTS.Application.Features.CashBook;
using OMTS.Application.Features.Purchasing.Receipts;
using OMTS.Application.Features.WaterQuality.Dtos;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Purchasing.Payments;

/// <summary>The part of a payment set against one Completed receipt of its supplier: whole VND above zero.</summary>
public sealed record PaymentAllocationInput(Guid? ReceiptId, decimal? Amount);

/// <param name="PaymentDate">The date paid; may be left out when <paramref name="PaidAt"/> is sent, and must then be its date at UTC+7.</param>
/// <param name="PaidAt">When the money was paid, never later than now. Without it: the payment date at the time now when it is today, or at the start of an earlier day.</param>
/// <param name="PaidById">Who paid: an Active Owner or staff account; the caller by default.</param>
/// <param name="Allocations">
/// Kind Payment only: the Completed receipts of the supplier it pays, one part per receipt, each at most what the receipt has left,
/// together at most the amount plus the discount. Left out: nothing is set against a receipt and the ledger reads the payment as covering the oldest receipts.
/// </param>
/// <param name="ConfirmAdvance">The Accountant answered Yes to MSG-PURCHASE-05: the payment is larger than the debt.</param>
/// <param name="ConfirmDuplicateBankReference">The Accountant answered Yes to MSG-PURCHASE-06: the bank reference is already recorded.</param>
/// <param name="DeduplicationKey">A repeated key returns the first payment and creates nothing.</param>
/// <param name="DiscountAmount">Debt the supplier writes off on this payment, with no money paid; kind Payment only.</param>
/// <param name="Code">
/// The code the user types: 1–30 ASCII letters, digits, "-", "_" or ".", unique among supplier payments regardless of case. Left
/// empty: the next TT code. It never changes once recorded.
/// </param>
/// <param name="CashAccountId">The bank account the money was paid from, on a bank transfer only: an active Bank account of the cash book.</param>
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
    string? DeduplicationKey = null,
    decimal DiscountAmount = 0,
    DateTimeOffset? PaidAt = null,
    Guid? PaidById = null,
    IReadOnlyList<PaymentAllocationInput>? Allocations = null,
    string? Code = null,
    Guid? CashAccountId = null);

/// <summary>
/// The edit of a Recorded payment's voucher: <see cref="Method"/> is required and, with <see cref="BankReference"/> and
/// <see cref="Note"/>, replaces the stored one (a transfer needs its bank reference; null clears the note). <see cref="PaidAt"/>
/// and <see cref="PaidById"/> left out keep theirs. The money never changes: <see cref="SupplierId"/>, <see cref="Kind"/>,
/// <see cref="Amount"/>, <see cref="DiscountAmount"/>, <see cref="Allocations"/> and <see cref="Code"/> may be sent, but only as stored.
/// <see cref="CashAccountId"/> replaces the stored account like the bank reference does: only with a bank transfer, empty clears it.
/// </summary>
public sealed record UpdateSupplierPaymentRequest(
    string? RowVersion,
    PaymentMethod? Method,
    string? BankReference = null,
    string? Note = null,
    DateTimeOffset? PaidAt = null,
    Guid? PaidById = null,
    bool ConfirmDuplicateBankReference = false,
    Guid? SupplierId = null,
    SupplierPaymentKind? Kind = null,
    decimal? Amount = null,
    decimal? DiscountAmount = null,
    IReadOnlyList<PaymentAllocationInput>? Allocations = null,
    string? Code = null,
    Guid? CashAccountId = null);

public sealed record CancelSupplierPaymentRequest(string? Reason, string? RowVersion = null);

/// <summary>A receipt the payment pays, as the ledger reads it now.</summary>
/// <param name="ReceiptPayable">Its total less its discount.</param>
/// <param name="PaidBefore">What the other payments cover of it now.</param>
/// <param name="Amount">The part of this payment set against it when recorded.</param>
/// <param name="ReceiptRemaining">What is left on it after every payment and return; 0 on a receipt cancelled since.</param>
public sealed record PaymentAllocationView(
    Guid ReceiptId, string ReceiptCode, DateTimeOffset ReceivedAt, decimal ReceiptPayable, decimal PaidBefore, decimal Amount, decimal ReceiptRemaining);

/// <param name="DebtAfter">On a write: the supplier debt after it (on a repeated deduplication key: the debt now). Null on the reads.</param>
/// <param name="PurchaseReturnId">The return this money came back for; null on any other payment.</param>
/// <param name="PaidAt">When the money was paid, at UTC+7; <paramref name="PaymentDate"/> is its date.</param>
/// <param name="PaidBy">Who paid; null on a payment that names no one.</param>
/// <param name="Allocations">The receipts it pays, in the order received; null on the list, which does not read them.</param>
/// <param name="CashAccount">The bank account of a bank transfer; null for cash or a transfer with no account named.</param>
public sealed record SupplierPaymentView(
    Guid Id, string Code, SupplierRef Supplier, SupplierPaymentKind Kind, decimal Amount, DateOnly PaymentDate, PaymentMethod Method,
    string? BankReference, string? Note, int StatusId, string Status, DateTimeOffset? CancelledAt, string? CancelReason,
    AccountRef RecordedBy, string RowVersion, decimal? DebtAfter = null, decimal DiscountAmount = 0, Guid? PurchaseReturnId = null,
    DateTimeOffset PaidAt = default, AccountRef? PaidBy = null, IReadOnlyList<PaymentAllocationView>? Allocations = null,
    CashAccountRef? CashAccount = null);

public sealed record SupplierPaymentQuery(
    Guid? SupplierId = null, SupplierPaymentKind? Kind = null, PaymentMethod? Method = null, int? StatusId = null,
    DateOnly? From = null, DateOnly? To = null, int Page = 1, int PageSize = 20);

/// <param name="TotalPaid">Σ the money of Recorded payments of kind Payment of every match, not only of the page; discounts are not money paid.</param>
/// <param name="TotalPaidBack">Σ Recorded payments of kind PaidBack, same scope.</param>
public sealed record SupplierPaymentPage(Paged<SupplierPaymentView> Page, decimal TotalPaid, decimal TotalPaidBack);
