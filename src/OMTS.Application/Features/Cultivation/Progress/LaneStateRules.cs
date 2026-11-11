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

/// <summary>A lane hung with seed in a seeding session of a cycle.</summary>
public sealed record LaneSeeding(Guid CycleId, Guid LaneId, DateOnly Date);

/// <summary>A lane in a harvest lot of a cycle; <paramref name="IsFinished"/> when the lot marks its harvest finished.</summary>
public sealed record LaneHarvest(Guid CycleId, Guid LaneId, DateOnly Date, bool IsFinished);

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
        IReadOnlyCollection<LaneHarvest> harvests)
    {
        if (cycle is null || !cycle.IsOpenOn(date))
        {
            return laneIds.ToDictionary(id => id, _ => LaneState.NotSeeded);
        }

        var seeded = seedings.Where(s => s.CycleId == cycle.Id && s.Date <= date).Select(s => s.LaneId).ToHashSet();
        var inLots = harvests.Where(h => h.CycleId == cycle.Id && h.Date <= date).ToList();
        var inALot = inLots.Select(h => h.LaneId).ToHashSet();
        var finished = inLots.Where(h => h.IsFinished).Select(h => h.LaneId).ToHashSet();

        return laneIds.ToDictionary(id => id, id =>
            finished.Contains(id) ? LaneState.Harvested
            : inALot.Contains(id) ? LaneState.BeingHarvested
            : seeded.Contains(id) ? LaneState.Growing
            : LaneState.NotSeeded);
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
