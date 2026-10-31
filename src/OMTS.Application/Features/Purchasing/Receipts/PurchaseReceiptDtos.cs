using OMTS.Application.Features.WaterQuality.Dtos;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Purchasing.Receipts;

/// <summary>
/// A line as entered (PURCHASE-01 step 3). On a Draft every field may be empty; completing needs the item, the quantity and
/// the unit price. <see cref="RaftId"/> only on a Seed or Material line, among the rafts In Use of the caller's zones (the
/// Owner: any); <see cref="SupplierLotCode"/> only on a Seed line (ignored on the others).
/// </summary>
public sealed record ReceiptLineInput(Guid? ItemId, decimal? Quantity, decimal? UnitPrice, string? SupplierLotCode = null, Guid? RaftId = null);

/// <summary>
/// PURCHASE-01 create / Draft edit. <see cref="Complete"/> false saves a Draft (only an In Use supplier is needed);
/// true checks everything and completes it in the same call. <see cref="ReceiptDate"/> defaults to today, never later.
/// </summary>
public sealed record SavePurchaseReceiptRequest(
    Guid? SupplierId,
    DateOnly? ReceiptDate,
    string? Notes,
    IReadOnlyList<ReceiptLineInput>? Lines,
    bool Complete = false,
    string? RowVersion = null);

public sealed record CompleteReceiptRequest(string? RowVersion);

/// <summary>PURCHASE-06: the reason is required; payments are never cancelled with the receipt.</summary>
public sealed record CancelReceiptRequest(string? Reason, string? RowVersion = null);

public sealed record SupplierRef(Guid Id, string Name, string PhoneNumber);

public sealed record RaftRef(Guid Id, string Code);

/// <summary>The raft of a line with its zone.</summary>
public sealed record LineRaftRef(Guid Id, string Code, Guid ZoneId, string ZoneCode);

/// <param name="SeedLotCode">PN000123-1 on a Seed line of a Completed receipt (GBR-04); null otherwise.</param>
/// <param name="Seeded">Quantity seeded from the seed lot (0 for other lines).</param>
/// <param name="Returnable">Received − seeded − already returned (0 on a Draft line without quantity).</param>
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
    decimal Returnable);

public sealed record ReceiptReturnView(Guid Id, string Code, DateOnly ReturnDate, string Reason, decimal ReturnValue, decimal RefundAmount, decimal DebtReductionAmount);

/// <summary>PURCHASE-03.1 / SCR-PURCHASE-02: the receipt with its lines and rafts, its returns and, when Cancelled, the reason.</summary>
/// <param name="Total">Σ line amounts, not rounded (BR-PURCHASE-02).</param>
/// <param name="Returned">Σ of its purchase returns.</param>
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
    string RowVersion);

/// <summary>Why a receipt cannot be cancelled (MSG-PURCHASE-09): <c>Seeded</c> with the seed lot code, or <c>Returned</c> with the return code.</summary>
public sealed record CancelBlock(string Kind, string Code, string Message, string MessageCode);
