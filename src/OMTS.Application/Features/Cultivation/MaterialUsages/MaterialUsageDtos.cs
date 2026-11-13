using OMTS.Application.Features.Cultivation.Cycles;

namespace OMTS.Application.Features.Cultivation.MaterialUsages;

/// <summary>One item of a repair and the quantity used.</summary>
public sealed record MaterialUsageLine(Guid? ItemId, decimal? Quantity);

/// <summary>CYCLE-03.0: one repair of the raft. It makes one record per item.</summary>
public sealed record RecordMaterialUsageRequest(DateOnly? UsageDate, string? RepairReason, IReadOnlyList<MaterialUsageLine>? Items);

/// <summary>CYCLE-03.1: the record as it should be, with the reason for the edit. Another <see cref="RaftId"/> moves it to that raft (BR-CYCLE-03).</summary>
public sealed record EditMaterialUsageRequest(
    Guid? RaftId, DateOnly? UsageDate, Guid? ItemId, decimal? Quantity, string? RepairReason, string? Reason, string? RowVersion);

/// <summary>CYCLE-03.2: removes a record, with the reason kept in the audit log and the version read (NFR-09).</summary>
public sealed record RemoveMaterialUsageRequest(string? Reason, string? RowVersion);

/// <summary>A usage record (BR-CYCLE-03). It has no price (BR-FARM-05).</summary>
/// <param name="CycleId">The cycle of the raft open on the date, worked out on read; null when there was none.</param>
/// <param name="CanEdit">The caller may edit it now: the raft and its zone are In Use and no closed cycle locks it (CYCLE-03.1).</param>
public sealed record MaterialUsageView(
    Guid Id,
    Guid RaftId,
    string RaftCode,
    Guid? CycleId,
    DateOnly UsageDate,
    string RepairReason,
    Guid ItemId,
    string ItemCode,
    string ItemName,
    string Unit,
    decimal Quantity,
    Guid RecordedByAccountId,
    string? RecordedByName,
    DateTimeOffset RecordedAt,
    bool CanEdit,
    string RowVersion,
    Guid? IssueId = null,
    string? IssueCode = null);

/// <summary>One repair on the screen: the records with the same date and reason (SCR-CYCLE-03).</summary>
public sealed record MaterialUsageRepair(DateOnly UsageDate, string RepairReason, IReadOnlyList<MaterialUsageView> Records);

/// <summary>The usage of one cycle, or of the dates with no cycle.</summary>
/// <param name="Cycle">The cycle; null for the separate group of dates when the raft had no cycle (BR-CYCLE-03).</param>
/// <param name="Locked">The cycle is closed, so these records can no longer change (BR-CYCLE-04).</param>
/// <param name="Repairs">The latest repair first.</param>
public sealed record MaterialUsageGroup(CycleListItem? Cycle, bool Locked, IReadOnlyList<MaterialUsageRepair> Repairs);

/// <summary>The Material usage screen of a raft (SCR-CYCLE-03).</summary>
/// <param name="CanRecord">Record usage is shown: the raft and its zone are In Use and the caller may record (CYCLE-03.0 step 2).</param>
/// <param name="Groups">The latest cycle first, the group with no cycle last. A cycle with no usage has no group.</param>
public sealed record MaterialUsageList(Guid RaftId, string RaftCode, bool CanRecord, IReadOnlyList<MaterialUsageGroup> Groups);

/// <summary>Answer of a recorded repair (CYCLE-03.0 step 7).</summary>
/// <param name="Records">One record per item, by item code.</param>
/// <param name="Message">MSG-GEN-05 "Material usage saved.".</param>
/// <param name="MessageCode">The code of <paramref name="Message"/>.</param>
public sealed record MaterialUsageRecorded(IReadOnlyList<MaterialUsageView> Records, string Message, string MessageCode);

/// <summary>Answer of an edit (CYCLE-03.1).</summary>
/// <param name="Message">MSG-GEN-05 "Material usage saved.".</param>
/// <param name="MessageCode">The code of <paramref name="Message"/>.</param>
public sealed record MaterialUsageSaved(MaterialUsageView Record, string Message, string MessageCode);
