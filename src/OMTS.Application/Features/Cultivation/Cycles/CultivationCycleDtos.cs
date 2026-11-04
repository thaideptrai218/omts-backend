using OMTS.Application.Features.Cultivation.Progress;
using OMTS.Application.Features.Farming.Rafts;

namespace OMTS.Application.Features.Cultivation.Cycles;

/// <summary>CYCLE-01.0. Without <see cref="StartDate"/> the cycle starts today; the plan estimates are optional.</summary>
public sealed record StartCycleRequest(DateOnly? StartDate, decimal? ExpectedSeedQuantity, DateOnly? ExpectedHarvestDate, decimal? TargetYieldKg);

/// <summary>CYCLE-01.1: the start date and the plan estimates as they should be; a plan estimate sent empty is cleared.</summary>
public sealed record EditCycleRequest(DateOnly? StartDate, decimal? ExpectedSeedQuantity, DateOnly? ExpectedHarvestDate, decimal? TargetYieldKg, string? RowVersion);

/// <summary>CYCLE-01.2.</summary>
public sealed record CancelCycleRequest(string? RowVersion);

/// <param name="DaysOfFarming">Both the first and the last day count; the last day of an Open cycle is today.</param>
/// <param name="CanEdit">The cycle is Open on a raft In Use (CYCLE-01 PRE-3).</param>
/// <param name="CanCancel">The cycle is Open on a raft In Use and has no record yet (CYCLE-01 PRE-3).</param>
public sealed record CycleView(
    Guid Id,
    Guid RaftId,
    string RaftCode,
    DateOnly StartDate,
    DateOnly? CloseDate,
    decimal? ExpectedSeedQuantity,
    DateOnly? ExpectedHarvestDate,
    decimal? TargetYieldKg,
    DateOnly? SeedingCompletedOn,
    string? Assessment,
    bool ClosedEarly,
    int DaysOfFarming,
    int StatusId,
    string Status,
    bool CanEdit,
    bool CanCancel,
    string RowVersion);

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
public sealed record CycleListItem(Guid Id, DateOnly StartDate, DateOnly? CloseDate, int StatusId, string Status, string RowVersion);

/// <summary>A raft on the Cultivation cycles screen (SCR-CYCLE-01). The commands are true only when the use case would accept them.</summary>
/// <param name="LaneCount">The number of lanes In Use.</param>
/// <param name="Cycle">The Open cycle, else the last closed one, else null.</param>
/// <param name="LastCloseDate">The close date of the raft's last closed cycle, or null.</param>
/// <param name="CanStart">In Use, no Open cycle, and the previous cycle was not closed today (CYCLE-01 PRE-3).</param>
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
    bool CanCancel);

/// <summary>A lane on a date: its status In Use or Removed, and the state worked out for that date (BR-FARM-03).</summary>
public sealed record LaneDayView(Guid Id, int LaneNumber, int StatusId, string Status, LaneState State);

/// <summary>CYCLE-05: the lanes of a raft on a date with the cycle that was open then.</summary>
/// <param name="Cycle">The cycle open on <paramref name="Date"/>, or null: the raft had none, so every lane is Not seeded.</param>
/// <param name="Lanes">Lanes In Use, and Removed lanes that have records in the cycle, in number order.</param>
/// <param name="NotSeededLanes">The numbers of the lanes In Use that are Not seeded on the date (BR-CYCLE-02).</param>
public sealed record CultivationProgressView(
    Guid RaftId, string RaftCode, DateOnly Date, CycleView? Cycle, IReadOnlyList<LaneDayView> Lanes, IReadOnlyList<int> NotSeededLanes);
