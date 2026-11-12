using OMTS.Application.Features.Cultivation.Closing;
using OMTS.Application.Features.Cultivation.CultivationLogs;
using OMTS.Application.Features.Cultivation.Progress;
using OMTS.Application.Features.Cultivation.Seeding;
using OMTS.Application.Features.Farming.Rafts;

namespace OMTS.Application.Features.Cultivation.Cycles;

/// <summary>
/// CYCLE-01.0. Without <see cref="StartDate"/> the cycle starts today; the plan estimates are optional. There is no expected harvest date any more (owner decision of 12/11/2026): a field sent for it is ignored.
/// The plan carries 1 to 3 photos, sent beside it; <paramref name="OtherFilePart"/>: a file came in a part not named <c>photos</c>.
/// </summary>
public sealed record StartCycleRequest(DateOnly? StartDate, decimal? ExpectedSeedQuantity, decimal? TargetYieldKg, bool OtherFilePart = false);

/// <summary>CYCLE-01.1: the start date and the plan estimates as they should be; a plan estimate sent empty is cleared.</summary>
public sealed record EditCycleRequest(DateOnly? StartDate, decimal? ExpectedSeedQuantity, decimal? TargetYieldKg, string? RowVersion);

/// <summary>CYCLE-01.2.</summary>
public sealed record CancelCycleRequest(string? RowVersion);

/// <param name="DaysOfFarming">Both the first and the last day count; the last day of an Open cycle is today, of a closed one its last harvest lot, or else the close date.</param>
/// <param name="CanEdit">The cycle is Open on a raft In Use in a zone In Use (CYCLE-01 PRE-3).</param>
/// <param name="CanCancel">The cycle can be edited and has no record yet (CYCLE-01 PRE-3).</param>
/// <param name="NurseryBatchId">The nursery batch the cycle was created from; null on a cycle started the old way (no nursery), whose other nursery fields are null too.</param>
/// <param name="NurseryAllocated">The pieces of the batch given to this cycle: seeding is capped by it. <paramref name="NurserySeeded"/> is what is hung so far, <paramref name="NurseryRemaining"/> what can still be.</param>
public sealed record CycleView(
    Guid Id,
    Guid RaftId,
    string RaftCode,
    DateOnly StartDate,
    DateOnly? CloseDate,
    decimal? ExpectedSeedQuantity,
    decimal? TargetYieldKg,
    DateOnly? SeedingCompletedOn,
    string? Assessment,
    bool ClosedEarly,
    int DaysOfFarming,
    int StatusId,
    string Status,
    bool CanEdit,
    bool CanCancel,
    string RowVersion,
    Guid? NurseryBatchId = null,
    string? NurseryCode = null,
    decimal? NurseryAllocated = null,
    decimal? NurserySeeded = null,
    decimal? NurseryRemaining = null,
    IReadOnlyList<CyclePlanPhotoView>? PlanPhotos = null,
    bool PlanPhotosFromNursery = false);

/// <summary>A photo of the plan of a cycle: <paramref name="Url"/> is a signed link that expires.</summary>
public sealed record CyclePlanPhotoView(Guid Id, int SortOrder, string Url);

/// <summary>
/// Change the photos of the plan of a cycle started without a nursery batch (decision of 12/11/2026): the plan then keeps exactly the photos of <paramref name="KeepPhotoIds"/> and the new ones, 1 to 3.
/// <paramref name="RowVersion"/> is the version of the cycle; <paramref name="OtherFilePart"/>: a file came in a part not named <c>photos</c>.
/// </summary>
public sealed record ChangePlanPhotosRequest(string? RowVersion, IReadOnlyList<Guid>? KeepPhotoIds, bool OtherFilePart = false);

/// <summary>Answer of a cycle edit (CYCLE-01.1).</summary>
/// <param name="Message">MSG-GEN-05 "Cultivation cycle saved.".</param>
/// <param name="MessageCode">The code of <paramref name="Message"/>.</param>
public sealed record CycleSaved(CycleView Cycle, string Message, string MessageCode);

/// <summary>Answer of a cycle start (CYCLE-01.0): the cycle, and the lanes and previous close date to check against it.</summary>
/// <param name="PreviousCloseDate">The close date of the raft's previous cycle, or null.</param>
/// <param name="Lanes">Every lane of the raft in number order, the Removed ones included.</param>
/// <param name="Message">MSG-GEN-05 "Cultivation cycle saved.".</param>
/// <param name="MessageCode">The code of <paramref name="Message"/>.</param>
public sealed record CycleStarted(CycleView Cycle, DateOnly? PreviousCloseDate, IReadOnlyList<LaneView> Lanes, string Message, string MessageCode);

/// <summary>A cycle in the list of a raft (CYCLE-05.1), the newest first.</summary>
/// <param name="ClosedEarly">A Closed cycle was closed early (BR-CYCLE-07); false for an Open one.</param>
/// <param name="NurseryBatchId">The nursery batch the cycle was created from; null on a cycle started the old way.</param>
public sealed record CycleListItem(Guid Id, DateOnly StartDate, DateOnly? CloseDate, int StatusId, string Status, string RowVersion, bool ClosedEarly, Guid? NurseryBatchId = null);

/// <summary>A raft on the Cultivation cycles screen (SCR-CYCLE-01). The commands are true only when the use case would accept them.</summary>
/// <param name="LaneCount">The number of lanes In Use.</param>
/// <param name="Cycle">The Open cycle, else the last closed one, else null.</param>
/// <param name="LastCloseDate">The close date of the raft's last closed cycle, or null.</param>
/// <param name="CanStart">In Use, no Open cycle, and the previous cycle was not closed today (CYCLE-01 PRE-3).</param>
/// <param name="CanClose">The raft has an Open cycle that can still be changed (CYCLE-06 PRE-2): the same condition as <paramref name="CanEdit"/>.</param>
public sealed record ZoneCycleItem(
    Guid RaftId,
    string RaftCode,
    int RaftStatusId,
    string RaftStatus,
    int LaneCount,
    CycleListItem? Cycle,
    DateOnly? LastCloseDate,
    bool CanStart,
    bool CanEdit,
    bool CanCancel,
    bool CanClose);

/// <summary>
/// A lane on a date: its status In Use or Removed, and the state worked out for that date (BR-FARM-03).
/// </summary>
/// <param name="IsDamaged">Damaged now and not repaired: the state is the one it had, and no seeding or harvest is possible. <paramref name="ReasonCode"/> says why it cannot be used.</param>
/// <param name="IsOutOfService">Put out of service (decision of 12/11/2026). The state shows <c>OutOfService</c> when seed hung on the lane in the cycle (the lane counts as finished at the close).</param>
/// <param name="LineLengthM">The line length in effect (the lane's own, else the raft's); null when unknown.</param>
/// <param name="ReasonCode">Why the lane cannot take seed or a harvest from its own flags: <c>LaneDamaged</c> or <c>LaneOutOfService</c>; null otherwise.</param>
/// <param name="SeededPercent">The share of the lane hung in the cycle, 0 to 100 (owner decision of 12/11/2026); the state follows from it and <paramref name="HarvestedPercent"/>.</param>
/// <param name="HarvestedPercent">The share harvested, 0 to 100; null when nobody entered it (a lane harvested in lots before the percentages).</param>
/// <param name="LastModifiedByName">Who last changed the percentages by hand, with <paramref name="LastModifiedAt"/>; null when nobody did. <paramref name="ProgressHistory"/> lists those changes, the newest first.</param>
public sealed record LaneDayView(
    Guid Id, int LaneNumber, int StatusId, string Status, LaneState State,
    bool IsDamaged = false, bool IsOutOfService = false, decimal? LineLengthM = null, string? ReasonCode = null,
    decimal SeededPercent = 0m, decimal? HarvestedPercent = null, string? LastModifiedByName = null, DateTimeOffset? LastModifiedAt = null,
    IReadOnlyList<LaneProgressChangeView>? ProgressHistory = null);

/// <summary>CYCLE-05: the lanes of a raft on a date with the cycle that was open then.</summary>
/// <param name="Cycle">The cycle open on <paramref name="Date"/>, or null: the raft had none, so every lane is Not seeded.</param>
/// <param name="Lanes">Lanes In Use, and Removed lanes that have records in the cycle, in number order.</param>
/// <param name="NotSeededLanes">The numbers of the lanes on this view, In Use and Not seeded on the date (BR-CYCLE-02).</param>
/// <param name="SeedLots">For each lane seeded on or before <paramref name="Date"/>, its seed lots and seeding dates (CYCLE-05.0); empty with no cycle.</param>
/// <param name="Logs">Every cultivation log of <paramref name="Cycle"/>, the latest visit first (CYCLE-05.0); empty with no cycle.</param>
/// <param name="Summary">For a Closed <paramref name="Cycle"/>, its summary on the close date with the assessment (CYCLE-05.1 step 2, CYCLE-05.2); null otherwise.</param>
public sealed record CultivationProgressView(
    Guid RaftId,
    string RaftCode,
    DateOnly Date,
    CycleView? Cycle,
    IReadOnlyList<LaneDayView> Lanes,
    IReadOnlyList<int> NotSeededLanes,
    IReadOnlyList<LaneSeedLots> SeedLots,
    IReadOnlyList<CultivationLogView> Logs,
    CycleSummary? Summary = null);
