using OMTS.Application.Features.Cultivation.Cycles;
using OMTS.Application.Features.Cultivation.Progress;

namespace OMTS.Application.Features.Farming.Rafts;

/// <summary>
/// Report a lane damaged (decision of 12/11/2026). <paramref name="Reason"/> is required (at most 500 characters); <paramref name="ReasonCode"/> is optional: LineBroken, BuoyDamaged, Storm,
/// MassMortality or Other. <paramref name="RowVersion"/> is the raft version the screen was read at (NFR-09). A photo is added after, with the incident photo route (optional, at most 3).
/// </summary>
public sealed record ReportLaneDamageRequest(string? Reason, string? ReasonCode, string? RowVersion);

/// <summary>A material a repair takes from the stock, in the unit of the item (<paramref name="Unit"/> is checked when sent, never converted).</summary>
public sealed record RepairMaterialInput(Guid? ItemId, decimal? Quantity, string? Unit);

/// <summary>
/// Record the repair of a damaged lane. <paramref name="Reason"/> says what was done (required). Each material is checked against the stock on hand (MSG-INPUT-01) and leaves the stock in the same
/// save, through a use issue linked to the raft and the incident; with none, the lane is simply marked repaired.
/// </summary>
public sealed record RepairLaneRequest(string? Reason, IReadOnlyList<RepairMaterialInput>? Materials, string? RowVersion);

/// <summary>
/// Put a lane out of service (it cannot be repaired). <paramref name="Reason"/> is required. When seed hangs on the lane the answer is a question first (409, <c>confirmFlag</c> <c>confirm</c>);
/// send <paramref name="Confirm"/> true to record the loss of that seed (no quantity). <paramref name="CycleRowVersion"/> is optional: the cycle version the screen was read at.
/// </summary>
public sealed record LaneOutOfServiceRequest(string? Reason, string? ReasonCode, bool Confirm, string? RowVersion, string? CycleRowVersion = null);

/// <summary>An optional photo of an incident; <paramref name="Url"/> is a signed link that expires.</summary>
public sealed record LaneIncidentPhotoView(Guid Id, int SortOrder, string Url);

/// <summary>A material a repair used, with the use issue it posted.</summary>
public sealed record LaneIncidentMaterialView(Guid ItemId, string ItemCode, string ItemName, string Unit, decimal Quantity, Guid? IssueId, string? IssueCode);

/// <summary>One entry of the history of a lane.</summary>
/// <param name="Kind"><c>Damaged</c>, <c>Repaired</c> or <c>OutOfService</c>.</param>
/// <param name="StateBefore">The lane state when it happened: NotSeeded, Growing, BeingHarvested or Harvested.</param>
/// <param name="HadSeed">An out-of-service entry for a lane that had seed in the Open cycle: the loss event of that seed, with no quantity.</param>
/// <param name="ResolvesIncidentId">The damage report a repair (or the out-of-service entry) closes.</param>
public sealed record LaneIncidentView(
    Guid Id,
    Guid LaneId,
    int LaneNumber,
    string Kind,
    DateTimeOffset OccurredAt,
    DateOnly OccurredOn,
    Guid? CycleId,
    string? ReasonCode,
    string Reason,
    string StateBefore,
    bool HadSeed,
    Guid RecordedByAccountId,
    string? RecordedByName,
    Guid? ResolvesIncidentId,
    IReadOnlyList<LaneIncidentPhotoView> Photos,
    IReadOnlyList<LaneIncidentMaterialView> Materials);

/// <summary>The history of the lanes of a raft, newest first.</summary>
public sealed record LaneHistory(Guid RaftId, string RaftCode, int? LaneNumber, IReadOnlyList<LaneIncidentView> Incidents);

/// <summary>
/// Answer of a lane command. <paramref name="Changed"/> is false when the lane was already in that condition (the call is repeated safely) and <paramref name="Incident"/> is then the entry that
/// put it there. <paramref name="Lane"/> is the lane as it is now, with its state of the day.
/// </summary>
/// <param name="RaftRowVersion">The new raft version, to send with the next lane command.</param>
/// <param name="CycleRowVersion">The new version of the Open cycle when the command moved it; null otherwise.</param>
/// <param name="EverySeededLaneHarvested">The cycle can now be closed normally (BR-CYCLE-07): every lane with seed is Harvested or out of service.</param>
public sealed record LaneIncidentSaved(
    LaneIncidentView Incident,
    LaneDayView Lane,
    bool Changed,
    string RaftRowVersion,
    string? CycleRowVersion,
    bool EverySeededLaneHarvested,
    string Message,
    string MessageCode);
