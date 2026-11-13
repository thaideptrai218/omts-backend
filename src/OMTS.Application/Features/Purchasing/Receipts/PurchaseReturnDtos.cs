using OMTS.Application.Common;
using OMTS.Application.Features.AccessManagement.Dtos;
using OMTS.Application.Features.Employees;
using OMTS.Application.Features.WaterQuality.Dtos;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Purchasing.Receipts;

/// <param name="UnitPrice">The price each unit is returned at, whole VND; left out: what one unit was paid (<c>netUnitPrice</c> of the receipt line).</param>
public sealed record ReturnLineInput(Guid? ReceiptLineId, decimal? Quantity, decimal? UnitPrice = null);

/// <summary>
/// PURCHASE-02.0 from a receipt, recorded at once. <see cref="ReturnedAt"/> is when the goods went back, never later than now
/// (default: now); the return date is its date at UTC+7. <see cref="OwnerApproved"/> is the Zone Manager's answer to
/// MSG-PURCHASE-02; the Owner never needs it. <see cref="Code"/> is the code the user types, as on <see cref="SavePurchaseReturnRequest"/>;
/// left empty the return gets the next TH code.
/// </summary>
public sealed record CreatePurchaseReturnRequest(
    string? Reason, IReadOnlyList<ReturnLineInput>? Lines, bool OwnerApproved = false, DateTimeOffset? ReturnedAt = null, string? Code = null);

/// <summary>
/// A line of a return: <see cref="ReceiptLineId"/> on a return from a receipt, or <see cref="ItemId"/> and <see cref="UnitPrice"/>
/// on one without. From a receipt, <see cref="UnitPrice"/> is optional, the price each unit is returned at: left out, a save
/// takes what one unit was paid (<c>netUnitPrice</c> of the receipt line) and an edit of a Recorded return keeps its price. A line of a receipt is returned in the unit of the receipt line;
/// without a receipt <see cref="ItemUnitId"/> is the unit of the item the line is entered in (empty: the base unit). Quantity and price are in that unit.
/// </summary>
public sealed record PurchaseReturnLineInput(Guid? ReceiptLineId, Guid? ItemId, decimal? UnitPrice, decimal? Quantity, Guid? ItemUnitId = null);

/// <summary>Money the supplier gives back when the return is completed: at most the value of the return.</summary>
public sealed record ReturnRefundInput(decimal? Amount, PaymentMethod? Method, string? BankReference = null);

/// <summary>
/// Saves a Draft or, with <see cref="Complete"/>, records the return in the same call. With <see cref="ReceiptId"/> the supplier and,
/// when left out, the zone come from the receipt; without it <see cref="SupplierId"/> is required. <see cref="ReturnedAt"/> is
/// when the goods went back, never later than now; left out it is now on a Draft save or a completion, and kept by the edit of a
/// Recorded return. The return date is its date at UTC+7. <see cref="Code"/> is the code the user types (1–30 ASCII letters,
/// digits, "-", "_" or "."; unique regardless of case); left empty, a new return gets the next TH code and a Draft keeps its own.
/// It may change while the return is a Draft; once Recorded only the stored code (or none) may be sent.
/// </summary>
public sealed record SavePurchaseReturnRequest(
    Guid? ReceiptId,
    Guid? SupplierId,
    Guid? ZoneId,
    string? Reason,
    decimal Discount = 0,
    bool OwnerApproved = false,
    bool Complete = false,
    ReturnRefundInput? Refund = null,
    IReadOnlyList<PurchaseReturnLineInput>? Lines = null,
    string? RowVersion = null,
    DateTimeOffset? ReturnedAt = null,
    string? Code = null);

/// <summary>
/// Records a saved Draft at its stored prices; the row version is optional because the status guards a second complete.
/// <see cref="ReturnedAt"/> as on a save: never later than now, now when left out.
/// </summary>
public sealed record CompletePurchaseReturnRequest(string? RowVersion = null, bool OwnerApproved = false, ReturnRefundInput? Refund = null, DateTimeOffset? ReturnedAt = null);

/// <summary>PURCHASE-02.1: the reason is required.</summary>
public sealed record CancelPurchaseReturnRequest(string? Reason, string? RowVersion = null);

/// <param name="ReceiptLineId">Null on a return without a receipt.</param>
public sealed record PurchaseReturnLineView(
    Guid? ReceiptLineId, Guid ItemId, string ItemCode, string ItemName, string Unit, decimal Quantity, decimal UnitPrice, decimal ReturnValue,
    Guid? ItemUnitId = null, decimal UnitFactor = 1, decimal? BaseQuantity = null);

/// <summary>A money-paid-back record made with the return; a Cancelled one stays listed with its status.</summary>
public sealed record ReturnRefundView(
    Guid Id, string Code, DateOnly PaymentDate, decimal Amount, PaymentMethod Method, string? BankReference, int StatusId, string Status, DateTimeOffset? CancelledAt);

public sealed record ReturnReceiptRef(Guid Id, string Code);

/// <param name="ReceiptId">Null on a return without a receipt, as are <paramref name="ReceiptCode"/> and <paramref name="Receipt"/>.</param>
/// <param name="ReturnValue">Σ line amounts as recorded; a later correction of the unit price does not change it (PM 03/11).</param>
/// <param name="NetValue"><paramref name="ReturnValue"/> less the discount: what it lowers the supplier debt by once Recorded.</param>
/// <param name="ReturnedAt">When the goods went back, at UTC+7; <paramref name="ReturnDate"/> is its date.</param>
public sealed record PurchaseReturnView(
    Guid Id,
    string Code,
    Guid? ReceiptId,
    string? ReceiptCode,
    ReturnReceiptRef? Receipt,
    SupplierRef Supplier,
    ZoneRef? Zone,
    DateOnly ReturnDate,
    string Reason,
    decimal Discount,
    int StatusId,
    string Status,
    DateTimeOffset? CancelledAt,
    string? CancelReason,
    AccountRef RecordedBy,
    decimal ReturnValue,
    decimal NetValue,
    IReadOnlyList<PurchaseReturnLineView> Lines,
    IReadOnlyList<ReturnRefundView> Refunds,
    string RowVersion,
    DateTimeOffset ReturnedAt = default);

/// <summary>The answer of a cancellation: the receipt when the return has one, otherwise the return itself.</summary>
public sealed record CancelledPurchaseReturn(PurchaseReceiptView? Receipt, PurchaseReturnView? Return);

/// <summary>The purchase return list filters; the period defaults to the current month, on the return date.</summary>
/// <param name="Search">Return code, receipt code or supplier name (contains).</param>
/// <param name="SortBy">code, date, supplier, total or status.</param>
/// <param name="SortDir">asc or desc.</param>
/// <param name="Source"><c>receipt</c>: made from a receipt; <c>standalone</c>: without one.</param>
public sealed record PurchaseReturnQuery(
    string? Search = null, DateOnly? From = null, DateOnly? To = null, Guid? SupplierId = null,
    int[]? StatusIds = null, Guid[]? ZoneIds = null, string? SortBy = null, string? SortDir = null, int Page = 1, int PageSize = 20, string? Source = null);

/// <param name="Value">Σ line amounts as recorded.</param>
/// <param name="NetValue"><paramref name="Value"/> less the discount.</param>
/// <param name="Refunded">Σ the Recorded money paid back for this return.</param>
/// <param name="ReturnedAt">When the goods went back, at UTC+7.</param>
public sealed record PurchaseReturnRow(
    Guid Id, string Code, DateOnly ReturnDate, Guid? ReceiptId, string? ReceiptCode, SupplierRef Supplier, string SupplierCode, ZoneRef? Zone, string Reason,
    int StatusId, string Status, AccountRef RecordedBy, decimal Value, decimal Discount, decimal NetValue, int ItemCount, decimal Refunded,
    DateTimeOffset? CancelledAt, string? CancelReason, DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt, string RowVersion, DateTimeOffset ReturnedAt = default);

/// <param name="TotalValue">Σ net value of the Recorded returns of every match, not only of the page.</param>
/// <param name="Message">MSG-GEN-10 when no return matches.</param>
public sealed record PurchaseReturnSearchResult(
    Paged<PurchaseReturnRow> Page, decimal TotalValue, DateOnly From, DateOnly To, string? Message, string? MessageCode);

