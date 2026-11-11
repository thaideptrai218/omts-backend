using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Enums;

namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>Issue list filters; null means "any". The period is on <c>IssuedAt</c>. A caller who sees only some zones passes them in <see cref="UseZoneIds"/>; disposals have no zone and show only with <see cref="IncludeDisposals"/>.</summary>
public sealed record InputIssueFilter(
    string? Search,
    InputIssueKind? Kind,
    IReadOnlyList<int>? StatusIds,
    DateTimeOffset? From,
    DateTimeOffset? Before,
    Guid? ZoneId,
    Guid? RaftId,
    Guid? IssueTypeId,
    Guid? CreatedById,
    Guid? ReceiverId,
    bool IncludeUse,
    IReadOnlySet<Guid>? UseZoneIds,
    bool IncludeDisposals,
    InputIssueSort Sort,
    bool Descending);

public enum InputIssueSort
{
    Code,
    IssuedAt,
    TotalValue,
    Status,
}

/// <summary>An issue of the list (zone, raft, type and receiver loaded, no lines) with the sum of its line values.</summary>
public sealed record InputIssueRow(InputIssue Issue, decimal TotalValue);

public sealed record InputIssueReceiver(Guid Id, string FullName);

public interface IInputIssueRepository
{
    /// <summary>The issue with its zone, raft, type, receiver and lines (items with their units, seed lots, rafts), tracked.</summary>
    Task<InputIssue?> GetForEditAsync(Guid issueId, CancellationToken cancellationToken);

    /// <summary>The issue that has the code ignoring case (any kind and status), other than the given one.</summary>
    Task<InputIssue?> FindByCodeAsync(string code, Guid? exceptIssueId, CancellationToken cancellationToken);

    /// <summary>Whether the employee is Employed and works in the zone.</summary>
    Task<bool> ReceiverWorksInAsync(Guid employeeId, Guid zoneId, CancellationToken cancellationToken);

    /// <summary>The employees who are Employed and work in the zone, by name.</summary>
    Task<IReadOnlyList<InputIssueReceiver>> ReceiversAsync(Guid zoneId, CancellationToken cancellationToken);

    Task<(IReadOnlyList<InputIssueRow> Rows, int Total, decimal TotalValue)> SearchAsync(InputIssueFilter filter, int page, int pageSize, CancellationToken cancellationToken);

    Task<IReadOnlyList<InputIssueRow>> ListAsync(InputIssueFilter filter, CancellationToken cancellationToken);

    /// <summary>Whether the receipt line is a line of a Completed receipt for this item (a seed lot of it).</summary>
    Task<bool> IsLotOfItemAsync(Guid itemId, Guid receiptLineId, CancellationToken cancellationToken);

    void Add(InputIssue issue);

    /// <summary>Deletes a Draft; its lines are removed with <see cref="RemoveLine"/> first.</summary>
    void Remove(InputIssue issue);

    /// <summary>New rows under a tracked issue are added explicitly: their keys are already set.</summary>
    void AddLine(InputIssueLine line);

    void RemoveLine(InputIssueLine line);
}

public interface IInputIssueTypeRepository
{
    /// <summary>The types that are not hidden, by name (system types first).</summary>
    Task<IReadOnlyList<InputIssueType>> ListActiveAsync(CancellationToken cancellationToken);

    Task<InputIssueType?> GetForEditAsync(Guid typeId, CancellationToken cancellationToken);

    /// <summary>The type that is not hidden and has the name ignoring case, other than the given one.</summary>
    Task<InputIssueType?> FindByNameAsync(string name, Guid? exceptTypeId, CancellationToken cancellationToken);

    /// <summary>Whether an issue has the type.</summary>
    Task<bool> IsUsedAsync(Guid typeId, CancellationToken cancellationToken);

    void Add(InputIssueType type);
    void Remove(InputIssueType type);
}

/// <summary>One movement of an item with the running balance computed when read.</summary>
public sealed record InputMovementRow(InputStockMovement Movement, decimal BalanceAfter, string? SeedLotCode);

public interface IInputMovementQuery
{
    /// <summary>
    /// The movements of an item oldest first with the balance after each, in the order of <c>OccurredAt</c> then write order. Without reversals, the rows an edit or cancellation
    /// undid and the reversal rows are left out together, so the balance still adds up.
    /// </summary>
    Task<IReadOnlyList<InputMovementRow>> ForItemAsync(Guid itemId, bool includeReversals, CancellationToken cancellationToken);

    /// <summary>The seed lots of an item (lines of Completed receipts with the item), oldest first, with the quantity of each on the ledger.</summary>
    Task<IReadOnlyList<InputSeedLotRow>> SeedLotsAsync(Guid itemId, CancellationToken cancellationToken);

    /// <summary>One seed lot of an item with its ledger quantity, or null when the line is not a lot of the item.</summary>
    Task<InputSeedLotRow?> SeedLotAsync(Guid itemId, Guid lotLineId, CancellationToken cancellationToken);
}

public sealed record InputSeedLotRow(
    Guid LineId, string Code, Guid ReceiptId, string ReceiptCode, DateTimeOffset ReceivedAt, string SupplierName, string? SupplierLotCode, decimal ReceivedQuantity, decimal OnHand);
