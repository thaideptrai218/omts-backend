using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Purchasing.Issues;

/// <summary>A line as entered: the item, the unit of the item it is typed in (empty: the base unit), the quantity in that unit, and for a Seed item the seed lot (empty: the quantity that has no lot).</summary>
public sealed record InputIssueLineInput(Guid? ItemId, Guid? ItemUnitId, decimal? Quantity, Guid? SeedLotLineId = null, string? Note = null);

/// <summary>
/// Saves an issue as a Draft or, with <see cref="Complete"/>, completes it in the same call; a Completed issue stays editable (<see cref="RowVersion"/> required) and its quantities are written again.
/// A use belongs to one zone and one raft of that zone (both required), needs an issue type to complete, and may name a receiver working in the zone; a disposal needs a reason to complete and has no zone, raft or receiver.
/// <see cref="Code"/>: 1–30 ASCII letters, digits, "-", "_" or "."; unique regardless of case; empty = the next XD/XH code, or the stored one on an update.
/// <see cref="IssuedAt"/> is when the items left, never later than now; empty = now, or the stored time on an update.
/// </summary>
public sealed record SaveInputIssueRequest(
    InputIssueKind? Kind,
    string? Code,
    DateTimeOffset? IssuedAt,
    Guid? ZoneId,
    Guid? RaftId,
    Guid? IssueTypeId,
    Guid? ReceiverEmployeeId,
    string? Reason,
    string? Notes,
    IReadOnlyList<InputIssueLineInput>? Lines,
    bool Complete = false,
    string? RowVersion = null);

public sealed record CompleteInputIssueRequest(string? RowVersion = null);

public sealed record CancelInputIssueRequest(string? Reason, string? RowVersion = null);

/// <param name="Unit">The unit the line was typed in; <paramref name="Quantity"/> is in it, <paramref name="UnitCost"/> is the cost of one such unit and <paramref name="Value"/> is quantity × cost.</param>
/// <param name="BaseQuantity">Quantity × unit factor, in <paramref name="BaseUnit"/>: what leaves the stock.</param>
public sealed record InputIssueLineView(
    Guid Id,
    int LineNumber,
    Guid ItemId,
    string ItemCode,
    string ItemName,
    InputItemGroup Group,
    Guid? ItemUnitId,
    string Unit,
    decimal UnitFactor,
    decimal Quantity,
    decimal BaseQuantity,
    string BaseUnit,
    Guid? SeedLotLineId,
    string? SeedLotCode,
    decimal UnitCost,
    decimal Value,
    string? Note);

public sealed record InputIssueView(
    Guid Id,
    InputIssueKind Kind,
    string Code,
    int StatusId,
    string Status,
    DateTimeOffset IssuedAt,
    Guid? ZoneId,
    string? ZoneCode,
    Guid? RaftId,
    string? RaftCode,
    Guid? IssueTypeId,
    string? IssueTypeName,
    Guid? ReceiverEmployeeId,
    string? ReceiverName,
    string? Reason,
    string? Notes,
    IReadOnlyList<InputIssueLineView> Lines,
    decimal TotalValue,
    DateTimeOffset CreatedAt,
    string? CreatedBy,
    DateTimeOffset? CompletedAt,
    DateTimeOffset? CancelledAt,
    string? CancelReason,
    string RowVersion);

/// <summary>A row of the issue list: the issue without its lines.</summary>
public sealed record InputIssueListItem(
    Guid Id,
    InputIssueKind Kind,
    string Code,
    int StatusId,
    string Status,
    DateTimeOffset IssuedAt,
    Guid? ZoneId,
    string? ZoneCode,
    Guid? RaftId,
    string? RaftCode,
    Guid? IssueTypeId,
    string? IssueTypeName,
    Guid? ReceiverEmployeeId,
    string? ReceiverName,
    string? Reason,
    string? Notes,
    decimal TotalValue,
    DateTimeOffset CreatedAt,
    string? CreatedBy,
    string RowVersion);

public enum InputIssueStatusFilter
{
    All,
    Draft,
    Completed,
    Cancelled,
}

/// <summary>The issue list and export query. The period is on the business date of <c>IssuedAt</c>. A Zone Manager sees only the use issues of its zones; the disposals are for the Owner and the Accountant.</summary>
/// <param name="Search">Text in the code, the notes or the reason.</param>
/// <param name="Kind">Use or Disposal.</param>
/// <param name="Status">All (the default), Draft, Completed or Cancelled.</param>
/// <param name="RaftId">Issues for the raft.</param>
/// <param name="CreatedById">The account that recorded the issue.</param>
/// <param name="SortBy">code (the default, newest first), issuedAt, totalValue or status.</param>
/// <param name="SortDir">asc or desc; the default is desc for the code and the others.</param>
public sealed record InputIssueListQuery(
    string? Search = null,
    InputIssueKind? Kind = null,
    InputIssueStatusFilter Status = InputIssueStatusFilter.All,
    DateOnly? From = null,
    DateOnly? To = null,
    Guid? ZoneId = null,
    Guid? RaftId = null,
    Guid? IssueTypeId = null,
    Guid? CreatedById = null,
    Guid? ReceiverId = null,
    string? SortBy = null,
    string? SortDir = null,
    int Page = 1,
    int PageSize = 20);

/// <param name="TotalValue">Σ the values of the Completed issues that match, not only the page.</param>
public sealed record InputIssueListSummary(decimal TotalValue);

public sealed record InputIssuePage(IReadOnlyList<InputIssueListItem> Items, int Page, int PageSize, int Total, InputIssueListSummary Summary);

public sealed record InputIssueTypeView(Guid Id, string Name, bool IsSystem, string RowVersion);

public sealed record SaveInputIssueTypeRequest(string? Name, string? RowVersion = null);

/// <summary>An employee who can receive items in the zone.</summary>
public sealed record InputIssueReceiverView(Guid Id, string FullName);

public sealed record InputIssueNextCode(string Code);

/// <summary>A movement of an item, quantities in its base unit. For an issue posted by a seeding session or a material usage, <c>sourceKind</c>, <c>sourceRecordId</c>, <c>cycleId</c>, <c>raftId</c> and <c>zoneId</c> name where it went; they are null for every other movement.</summary>
public sealed record InputMovementView(
    Guid Id,
    long Sequence,
    InputStockMovementKind Kind,
    Guid? DocumentId,
    string DocumentCode,
    DateTimeOffset OccurredAt,
    decimal Quantity,
    decimal BalanceAfter,
    decimal? UnitCost,
    decimal? TransactionPrice,
    string? PartyName,
    Guid? SeedLotLineId,
    string? SeedLotCode,
    Guid? ReversesMovementId,
    InputIssueSource? SourceKind = null,
    Guid? SourceRecordId = null,
    Guid? CycleId = null,
    Guid? RaftId = null,
    Guid? ZoneId = null);

public sealed record InputMovementPage(IReadOnlyList<InputMovementView> Items, int Page, int PageSize, int Total, string Unit);

/// <param name="OnHand">The quantity of the lot on the ledger, base unit: what the receipt, its returns and the issues left of it. Seeding records do not post movements.</param>
public sealed record InputSeedLotView(
    Guid SeedLotLineId, string Code, Guid ReceiptId, string ReceiptCode, DateTimeOffset ReceivedAt, string SupplierName, string? SupplierLotCode, decimal ReceivedQuantity, decimal OnHand);

/// <param name="UnlottedQuantity">The quantity on hand that has no lot (an opening quantity, and issues made without a lot): on hand − Σ the lots.</param>
/// <param name="SuggestedLotId">The oldest lot with a quantity left, what an issue line defaults to; null when none has.</param>
public sealed record InputSeedLotsView(IReadOnlyList<InputSeedLotView> Lots, decimal UnlottedQuantity, Guid? SuggestedLotId, string Unit);

/// <param name="DisposalId">The disposal that wrote the rest of the item off; null when nothing was left.</param>
public sealed record InputItemDeleted(Guid? DisposalId, string? DisposalCode, decimal DisposedQuantity);
