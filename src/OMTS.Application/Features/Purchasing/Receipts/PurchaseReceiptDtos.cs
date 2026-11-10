using OMTS.Application.Features.Employees;
using OMTS.Application.Features.WaterQuality.Dtos;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Purchasing.Receipts;

/// <summary>
/// A line as entered (PURCHASE-01 step 3). On a Draft every field may be empty; completing needs the item, the quantity and
/// the unit price. <see cref="Discount"/> is whole VND off each unit, 0 (when left out) to the unit price.
/// <see cref="RaftId"/> is optional, only on a Seed or Material line, among the rafts In Use of the caller's zones (the Owner:
/// any); <see cref="SupplierLotCode"/> only on a Seed line (ignored on the others). <see cref="LineId"/> names the line kept
/// when a Completed receipt is edited (a line without it is new); a Draft save ignores it and replaces every line.
/// </summary>
public sealed record ReceiptLineInput(
    Guid? ItemId, decimal? Quantity, decimal? UnitPrice, string? SupplierLotCode = null, Guid? RaftId = null, decimal? Discount = null, Guid? LineId = null);

/// <summary>
/// PURCHASE-01 create, Draft edit and edit of a Completed receipt. <see cref="Complete"/> false saves a Draft (only an In Use
/// supplier is needed); true checks everything and completes it in the same call; it is ignored on a Completed receipt.
/// <see cref="ReceivedAt"/> is when the goods were received, never later than now; the date received is its date at UTC+7.
/// Without it, the date received is <see cref="ReceiptDate"/> (default today, never later) at the time now, or at the start of
/// that day when it is an earlier day; an edit of a Completed receipt that sends neither keeps its time. <see cref="ReceivedById"/>
/// is an active Owner or staff account (default: the caller; kept by an edit of a Completed receipt). <see cref="Discount"/> is
/// whole VND, at most the total when completing. <see cref="ZoneId"/> is optional; when given it must be In Use.
/// <see cref="Code"/> is the code the user types (1–30 ASCII letters, digits, "-", "_" or "."; unique regardless of case); left
/// empty, a new receipt gets the next PN code and a Draft keeps its own. It may change while the receipt is a Draft; once Completed
/// only the stored code (or none) may be sent, since its seed lot codes are printed.
/// </summary>
public sealed record SavePurchaseReceiptRequest(
    Guid? SupplierId,
    DateOnly? ReceiptDate,
    string? Notes,
    IReadOnlyList<ReceiptLineInput>? Lines,
    bool Complete = false,
    string? RowVersion = null,
    decimal Discount = 0,
    Guid? ZoneId = null,
    DateTimeOffset? ReceivedAt = null,
    Guid? ReceivedById = null,
    string? Code = null);

public sealed record CompleteReceiptRequest(string? RowVersion);

/// <summary>PURCHASE-06: the reason is required; payments are never cancelled with the receipt.</summary>
public sealed record CancelReceiptRequest(string? Reason, string? RowVersion = null);

/// <summary>A line to correct; a value left out (null) keeps the line's value. An empty lot code clears it.</summary>
public sealed record CorrectionLineInput(Guid? LineId, decimal? Quantity, decimal? UnitPrice, string? SupplierLotCode = null);

/// <summary>PURCHASE-06.2: the Owner corrects lines of a receipt that can no longer be cancelled, with a reason (an edit through PUT can do more).</summary>
public sealed record CorrectReceiptRequest(string? Reason, IReadOnlyList<CorrectionLineInput>? Lines, string? RowVersion = null);

/// <param name="Code">The supplier code: links to the supplier list search by it, never by the name.</param>
public sealed record SupplierRef(Guid Id, string Name, string PhoneNumber, string? Code = null);

public sealed record RaftRef(Guid Id, string Code);

/// <summary>The raft of a line with its zone.</summary>
public sealed record LineRaftRef(Guid Id, string Code, Guid ZoneId, string ZoneCode);

/// <param name="SeedLotCode">PN000123-1 on a Seed line of a Completed receipt (GBR-04); null otherwise.</param>
/// <param name="Seeded">Quantity seeded from the seed lot (0 for other lines).</param>
/// <param name="ReturnedQuantity">Quantity on its Recorded returns.</param>
/// <param name="Returnable">Received − seeded − already returned (0 on a Draft line without quantity).</param>
/// <param name="LineAmount">Quantity × (unit price − discount), half up to the dong; null while the line has no quantity or no unit price.</param>
/// <param name="Discount">Whole VND off each unit.</param>
/// <param name="NetUnitPrice">
/// What one unit was paid, the price a return of the line defaults to: unit price less discount, less its share of the receipt
/// discount prorated by line amount, half up to the dong; null while the line has no unit price.
/// </param>
public sealed record ReceiptLineView(
    Guid Id,
    int LineNumber,
    Guid? ItemId,
    string? ItemCode,
    string? ItemName,
    InputItemGroup? Group,
    string Unit,
    decimal? Quantity,
    decimal? UnitPrice,
    decimal? LineAmount,
    string? SupplierLotCode,
    LineRaftRef? Raft,
    string? SeedLotCode,
    decimal Seeded,
    decimal ReturnedQuantity,
    decimal Returnable,
    decimal Discount = 0,
    decimal? NetUnitPrice = null);

/// <summary>A purchase return of the receipt, Cancelled ones included (with their reason) and Drafts left out; <paramref name="ReturnValue"/> is the amount as recorded.</summary>
/// <param name="ReturnedAt">When the goods went back, at UTC+7; <paramref name="ReturnDate"/> is its date.</param>
public sealed record ReceiptReturnView(
    Guid Id,
    string Code,
    DateOnly ReturnDate,
    string Reason,
    decimal ReturnValue,
    int StatusId,
    string Status,
    DateTimeOffset? CancelledAt,
    string? CancelReason,
    AccountRef RecordedBy,
    string RowVersion,
    decimal Discount = 0,
    decimal NetValue = 0,
    DateTimeOffset ReturnedAt = default);

/// <summary>PURCHASE-03.1 / SCR-PURCHASE-02: the receipt with its lines and rafts, its returns and, when Cancelled, the reason.</summary>
/// <param name="Total">Σ line amounts, each rounded half up to the dong (BR-PURCHASE-02).</param>
/// <param name="Returned">Σ of its Recorded purchase returns as recorded, each less its own discount.</param>
/// <param name="Payable">Total less discount.</param>
/// <param name="Paid">What the payments of the supplier cover of this receipt: the parts set against it first, then the rest oldest receipts first.</param>
/// <param name="ReceivedAt">When the goods were received, at UTC+7; <paramref name="ReceiptDate"/> is its date.</param>
/// <param name="ReceivedBy">Who took the goods in; null only on a receipt that has no receiver.</param>
public sealed record PurchaseReceiptView(
    Guid Id,
    string Code,
    SupplierRef Supplier,
    DateOnly ReceiptDate,
    decimal Total,
    decimal Returned,
    string? Notes,
    int StatusId,
    string Status,
    AccountRef RecordedBy,
    DateTimeOffset? CompletedAt,
    DateTimeOffset? CancelledAt,
    string? CancelReason,
    IReadOnlyList<ReceiptLineView> Lines,
    IReadOnlyList<ReceiptReturnView> Returns,
    string RowVersion,
    decimal Discount = 0,
    decimal Payable = 0,
    decimal Paid = 0,
    ZoneRef? Zone = null,
    DateTimeOffset ReceivedAt = default,
    AccountRef? ReceivedBy = null);

/// <summary>
/// PURCHASE-01.4: a new receipt filled from another one, line discounts included and the receiver left for the caller; nothing is saved until Save draft or Complete. A supplier, a zone, an
/// item or a raft that is no longer In Use, or a raft the caller may not choose, is left empty and named in
/// <see cref="LeftEmpty"/> (e.g. "Supplier", "Zone", "Line 2 item", "Line 1 raft").
/// </summary>
public sealed record PurchaseReceiptCopy(Guid CopiedFromId, string CopiedFromCode, SavePurchaseReceiptRequest Receipt, IReadOnlyList<string> LeftEmpty);

/// <summary>Why a receipt cannot be cancelled (MSG-PURCHASE-09): <c>Seeded</c> with the seed lot code, or <c>Returned</c> with the return code.</summary>
public sealed record CancelBlock(string Kind, string Code, string Message, string MessageCode);

/// <summary>
/// PURCHASE-06 step 2: the supplier debt before and after the cancellation; <see cref="Warning"/> is MSG-PURCHASE-07 when it
/// would then be below zero (<see cref="AdvanceAfter"/> paid in advance). <see cref="Blocked"/> when it cannot be cancelled.
/// </summary>
public sealed record CancelPreview(
    bool CanCancel, CancelBlock? Blocked, decimal DebtBefore, decimal DebtAfter, decimal AdvanceAfter, string? Warning, string? WarningCode);
