using OMTS.Application.Features.WaterQuality.Dtos;

namespace OMTS.Application.Features.Purchasing.Receipts;

public sealed record ReturnLineInput(Guid? ReceiptLineId, decimal? Quantity);

/// <summary>
/// PURCHASE-02.0. The return date is the day it is recorded and the amount comes from the unit prices of the receipt, so the
/// client sends neither. <see cref="OwnerApproved"/> is the Zone Manager's answer to MSG-PURCHASE-02; the Owner never needs it.
/// </summary>
public sealed record CreatePurchaseReturnRequest(string? Reason, IReadOnlyList<ReturnLineInput>? Lines, bool OwnerApproved = false);

/// <summary>PURCHASE-02.1: the reason is required.</summary>
public sealed record CancelPurchaseReturnRequest(string? Reason, string? RowVersion = null);

public sealed record PurchaseReturnLineView(Guid ReceiptLineId, string ItemCode, string ItemName, string Unit, decimal Quantity, decimal ReturnValue);

/// <param name="ReturnValue">Σ line amounts as recorded; a later correction of the unit price does not change it (PM 03/11).</param>
public sealed record PurchaseReturnView(
    Guid Id,
    string Code,
    Guid ReceiptId,
    string ReceiptCode,
    DateOnly ReturnDate,
    string Reason,
    int StatusId,
    string Status,
    DateTimeOffset? CancelledAt,
    string? CancelReason,
    AccountRef RecordedBy,
    decimal ReturnValue,
    IReadOnlyList<PurchaseReturnLineView> Lines,
    string RowVersion);
