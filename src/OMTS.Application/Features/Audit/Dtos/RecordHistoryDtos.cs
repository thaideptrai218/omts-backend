namespace OMTS.Application.Features.Audit.Dtos;

/// <summary>One changed field of a history entry: the name of the stored field, its Vietnamese label, the old and new value as text (null when absent) and the unit, when the field has one.</summary>
public sealed record RecordHistoryChange(string Field, string Label, string? From, string? To, string? Unit);

/// <summary>
/// One event in the history of a record (decision of 12/11/2026), built from the audit snapshots.
/// </summary>
/// <param name="Action">Created, Edited, Removed, LaneFinished, ... (the audit action without its record type; Corrected and Updated are Edited).</param>
/// <param name="ActionCode">The audit action as stored, e.g. HarvestLot.Corrected.</param>
/// <param name="ActionLabel">The Vietnamese name of the action.</param>
/// <param name="ByName">Who acted: the account or employee, "Hệ thống" for the system.</param>
/// <param name="ByRole">The Vietnamese name of the role of who acted when it happened (Chủ hợp tác xã, Quản lý vùng, Kế toán, Nhân viên, Thương nhân), or null.</param>
/// <param name="ByRoleCode">CooperativeOwner, ZONE_MANAGER, ACCOUNTANT, Staff, Employee, Trader or null.</param>
/// <param name="Note">Something the reader should know about the event, e.g. the published lots it changed; null mostly.</param>
public sealed record RecordHistoryEntry(
    Guid EventId,
    DateTimeOffset At,
    string ByName,
    string? ByRole,
    string? ByRoleCode,
    string Action,
    string ActionCode,
    string ActionLabel,
    string? Reason,
    string? Note,
    IReadOnlyList<RecordHistoryChange> Changes);

/// <summary>The history of one record, the oldest event first.</summary>
public sealed record RecordHistory(string RecordType, Guid RecordId, string? RecordCode, IReadOnlyList<RecordHistoryEntry> Entries);
