using OMTS.Domain.Entities.Farming;

namespace OMTS.Application.Features.Cultivation.Progress;

/// <summary>The state of a lane on a date (BR-FARM-03): derived on every read, never stored.</summary>
public enum LaneState
{
    NotSeeded,
    Growing,
    BeingHarvested,
    Harvested,

    /// <summary>
    /// The lane had seed in the cycle (Growing or Being harvested) and was put out of service (decision of 12/11/2026): the seed is lost, no quantity is recorded, and the lane counts as
    /// finished when the cycle closes. A lane Harvested before keeps that state; a lane with no seed stays Not seeded and shows its out-of-service flag instead.
    /// </summary>
    OutOfService,
}

/// <summary>A lane hung with seed in a seeding session of a cycle, with the share it took (100 by default) and when the session was recorded.</summary>
public sealed record LaneSeeding(Guid CycleId, Guid LaneId, DateOnly Date, decimal Percent = 100m, DateTimeOffset RecordedAt = default, Guid RecordId = default);

/// <summary>A lane in a harvest lot of a cycle; <paramref name="IsFinished"/> when the lot marks its harvest finished. <paramref name="Percent"/> is the share the lot took, null when nobody entered it.</summary>
public sealed record LaneHarvest(Guid CycleId, Guid LaneId, DateOnly Date, bool IsFinished, decimal? Percent = null, DateTimeOffset RecordedAt = default, Guid RecordId = default);

/// <summary>BR-FARM-03 as a pure function, so the AC-03 sequence is unit tested. Every consumer of a lane state goes through it.</summary>
public static class LaneStateRules
{
    /// <summary>
    /// The state of each lane on <paramref name="date"/>, from the records of <paramref name="cycle"/> dated up to that day.
    /// With no cycle open on that date every lane is Not seeded. Harvested once a lot marks the lane finished, Being harvested
    /// once it is in a lot, Growing once seed is hung on it. Records of another cycle do not count.
    /// </summary>
    public static IReadOnlyDictionary<Guid, LaneState> StatesOn(
        DateOnly date,
        CycleSpan? cycle,
        IEnumerable<Guid> laneIds,
        IReadOnlyCollection<LaneSeeding> seedings,
        IReadOnlyCollection<LaneHarvest> harvests,
        IReadOnlyCollection<LaneProgressStep>? steps = null)
    {
        if (cycle is null || !cycle.IsOpenOn(date))
        {
            return laneIds.ToDictionary(id => id, _ => LaneState.NotSeeded);
        }

        var seedingsOfCycle = seedings.Where(s => s.CycleId == cycle.Id).ToList();
        var lotsOfCycle = harvests.Where(h => h.CycleId == cycle.Id).ToList();
        var stepsOfCycle = (steps ?? []).Where(s => s.CycleId == cycle.Id).ToList();
        var inALot = lotsOfCycle.Where(h => h.Date <= date).Select(h => h.LaneId).ToHashSet();

        // The state follows from the percentages of the lane (owner decision of 12/11/2026); with no percentage entered anywhere it is what it always was.
        return laneIds.ToDictionary(id => id, id => LanePercentRules.StateOf(LanePercentRules.Of(id, date, seedingsOfCycle, lotsOfCycle, stepsOfCycle), inALot.Contains(id)));
    }

    /// <summary>The totals of every lane on a date, as <see cref="StatesOn"/> sees them: Not seeded with no cycle open on the date.</summary>
    public static IReadOnlyDictionary<Guid, LanePercents> PercentsOn(
        DateOnly date,
        CycleSpan? cycle,
        IEnumerable<Guid> laneIds,
        IReadOnlyCollection<LaneSeeding> seedings,
        IReadOnlyCollection<LaneHarvest> harvests,
        IReadOnlyCollection<LaneProgressStep> steps)
    {
        if (cycle is null || !cycle.IsOpenOn(date))
        {
            return laneIds.ToDictionary(id => id, _ => new LanePercents(0m, 0m));
        }

        var seedingsOfCycle = seedings.Where(s => s.CycleId == cycle.Id).ToList();
        var lotsOfCycle = harvests.Where(h => h.CycleId == cycle.Id).ToList();
        var stepsOfCycle = steps.Where(s => s.CycleId == cycle.Id).ToList();
        return laneIds.ToDictionary(id => id, id => LanePercentRules.Of(id, date, seedingsOfCycle, lotsOfCycle, stepsOfCycle));
    }

    /// <summary>
    /// A lane put out of service on or before <paramref name="date"/> that had seed (Growing or Being harvested) shows <see cref="LaneState.OutOfService"/>: its seed is lost. Nothing else changes:
    /// a lane Harvested keeps that, a lane with no seed stays Not seeded, and a date before the day it went out of service is as it was.
    /// </summary>
    public static IReadOnlyDictionary<Guid, LaneState> WithOutOfService(IReadOnlyDictionary<Guid, LaneState> states, IEnumerable<Lane> lanes, DateOnly date)
    {
        var lost = lanes.Where(l => l.OutOfServiceOn is { } on && on <= date && states.TryGetValue(l.Id, out var state) && state is LaneState.Growing or LaneState.BeingHarvested)
            .Select(l => l.Id).ToList();
        if (lost.Count == 0)
        {
            return states;
        }

        var changed = new Dictionary<Guid, LaneState>(states);
        foreach (var id in lost)
        {
            changed[id] = LaneState.OutOfService;
        }

        return changed;
    }

    /// <summary>A lane that is Harvested cannot be chosen in another lot of the cycle, nor can one with no seed or one out of service (BR-HARVEST-01).</summary>
    public static bool CanBeHarvested(LaneState state) => state is LaneState.Growing or LaneState.BeingHarvested;

    /// <summary>
    /// BR-CYCLE-07: at least one lane has seed and every lane with seed is Harvested, or out of service (decision of 12/11/2026: its seed is a loss event and the lane is finished).
    /// <paramref name="lanesInUse"/> holds the states of the lanes In Use only (BR-CYCLE-02).
    /// </summary>
    public static bool EverySeededLaneHarvested(IEnumerable<LaneState> lanesInUse)
    {
        var seeded = lanesInUse.Where(state => state != LaneState.NotSeeded).ToList();
        return seeded.Count > 0 && seeded.All(state => state is LaneState.Harvested or LaneState.OutOfService);
    }

    /// <summary>The lanes with a record in the cycle: a Removed one among them stays on the view of that cycle (BR-FARM-04).</summary>
    public static IReadOnlySet<Guid> LanesWithRecords(
        CycleSpan? cycle, IReadOnlyCollection<LaneSeeding> seedings, IReadOnlyCollection<LaneHarvest> harvests) =>
        cycle is null
            ? new HashSet<Guid>()
            : seedings.Where(s => s.CycleId == cycle.Id).Select(s => s.LaneId)
                .Concat(harvests.Where(h => h.CycleId == cycle.Id).Select(h => h.LaneId))
                .ToHashSet();
}
