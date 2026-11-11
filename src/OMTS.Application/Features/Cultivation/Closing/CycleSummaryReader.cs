using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Cultivation.Cycles;
using OMTS.Application.Features.Cultivation.Progress;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Application.Features.Cultivation.Closing;

/// <summary>
/// Works out the cycle summary (BR-CYCLE-06) from the seeding sessions and harvest lots of the cycle, and the lane states (BR-FARM-03) on the day it is read for,
/// in this one place: the close preview, the close itself, the closed-cycle views and the notification all show the same figures.
/// </summary>
public sealed class CycleSummaryReader(
    IRaftRepository rafts,
    ISeedingRecordRepository seedingRecords,
    IHarvestLotRepository harvestLots,
    LaneStateReader laneStates,
    TimeProvider timeProvider)
{
    /// <summary>The summary of a cycle, which comes with its raft: of a Closed cycle as of its close date, of an Open one as if it closed today.</summary>
    public async Task<CycleSummary> ReadAsync(CultivationCycle cycle, CancellationToken cancellationToken)
    {
        var today = timeProvider.Today();
        var asOf = cycle.CloseDate ?? today;
        var raft = await rafts.GetForEditAsync(cycle.RaftId, cancellationToken) ?? cycle.Raft;
        var sessions = await seedingRecords.ListByCycleAsync(cycle.Id, cancellationToken);
        var lots = await harvestLots.ListByCycleAsync(cycle.Id, cancellationToken);
        var states = (await laneStates.ReadAsync(raft, asOf, cancellationToken)).States;

        var lastLot = lots.Count == 0 ? (DateOnly?)null : lots.Max(l => l.HarvestDate);
        var notHarvested = NotHarvestedLanes(raft, states);
        var isOpen = cycle.StatusId == StatusIds.CultivationCycle.Open;
        var early = isOpen ? IsEarly(states) : cycle.ClosedEarly;
        return new CycleSummary(
            cycle.Id,
            raft.Code,
            cycle.StartDate,
            cycle.CloseDate,
            asOf,
            sessions.Count,
            sessions.Sum(s => s.Quantity),
            sessions.Select(s => s.SeedLotLine.Item?.Unit ?? s.SeedLotLine.Unit).FirstOrDefault(u => !string.IsNullOrWhiteSpace(u)),
            lots.Count,
            lots.Sum(l => l.EstimatedKg),
            lastLot,
            CycleDaysOfFarming.Count(cycle.StartDate, lastLot, asOf, today),
            cycle.ExpectedSeedQuantity,
            cycle.ExpectedHarvestDate,
            cycle.TargetYieldKg,
            early,
            early ? sessions.Count == 0 ? EarlyCloseReason.NoSeedingSession : EarlyCloseReason.LanesNotHarvested : null,
            notHarvested,
            isOpen ? null : cycle.Assessment,
            lots.Sum(l => l.CageCount),
            cycle.TargetYieldKg is { } target ? target - lots.Sum(l => l.EstimatedKg) : null,
            Seeding.SeedingPlanRules.Percent(cycle.ExpectedSeedQuantity, sessions.Sum(s => s.Quantity)));
    }

    /// <summary>
    /// BR-CYCLE-07: a close is normal when at least one lane has seed and every lane with seed is Harvested; lanes are not added or removed while a cycle
    /// is Open, so every lane of the raft counts. The same rule as the reminder of HARVEST-01 (MSG-CYCLE-08).
    /// </summary>
    public static bool IsEarly(IReadOnlyDictionary<Guid, LaneState> states) => !LaneStateRules.EverySeededLaneHarvested(states.Values);

    /// <summary>The lanes with seed that are not Harvested, Growing or Being harvested, by number.</summary>
    private static IReadOnlyList<int> NotHarvestedLanes(Raft raft, IReadOnlyDictionary<Guid, LaneState> states) => raft.Lanes
        .Where(l => states.TryGetValue(l.Id, out var state) && state is LaneState.Growing or LaneState.BeingHarvested)
        .Select(l => l.LaneNumber)
        .Order()
        .ToList();
}

