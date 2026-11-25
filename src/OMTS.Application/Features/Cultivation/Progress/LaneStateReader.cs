using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Application.Features.Cultivation.Progress;

/// <summary>The lane states of a raft on a date and the cycle they come from.</summary>
/// <param name="Cycle">The cycle open on the date, or null when every lane is Not seeded.</param>
/// <param name="LanesWithRecords">The lanes with a record in that cycle.</param>
/// <param name="Percents">The share hung and harvested of each lane (0 and 0 with no cycle); the state follows from them.</param>
/// <param name="Steps">The manual changes of the lanes of the cycle, the oldest first: the history shown with the lane.</param>
public sealed record LaneDay(
    CycleSpan? Cycle,
    IReadOnlyDictionary<Guid, LaneState> States,
    IReadOnlySet<Guid> LanesWithRecords,
    IReadOnlyDictionary<Guid, LanePercents>? Percents = null,
    IReadOnlyList<LaneProgressStep>? Steps = null);

/// <summary>
/// Reads the records <see cref="LaneStateRules"/> works from, in this one place: the seeding sessions and the harvest lots of the cycle, and the manual changes of the lane percentages.
/// </summary>
public sealed class LaneStateReader(ICultivationCycleQuery cycles, ISeedingRecordRepository seedingRecords, IHarvestLotRepository harvestLots, ILaneProgressRepository progress)
{
    public async Task<LaneDay> ReadAsync(Raft raft, DateOnly date, CancellationToken cancellationToken)
    {
        var cycle = await cycles.CycleOnDateAsync(raft.Id, date, cancellationToken);
        var seedings = cycle is null ? [] : await seedingRecords.LaneSeedingsAsync(cycle.Id, cancellationToken);
        var harvests = cycle is null ? [] : await harvestLots.LaneHarvestsAsync(cycle.Id, cancellationToken);
        var steps = cycle is null ? [] : await progress.StepsAsync(cycle.Id, cancellationToken);
        return DayOf(raft, date, cycle, seedings, harvests, steps);
    }

    /// <summary>
    /// The same for several rafts, the lane day of each raft by its id, in four reads however many rafts there are (the cycles of the date, then the seedings, harvests and steps of all those cycles).
    /// Each lane day equals what <see cref="ReadAsync"/> gives for the raft.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, LaneDay>> ReadManyAsync(IReadOnlyCollection<Raft> rafts, DateOnly date, CancellationToken cancellationToken)
    {
        var cycleOf = await cycles.CyclesOnDateAsync(rafts.Select(r => r.Id).ToList(), date, cancellationToken);
        var cycleIds = cycleOf.Values.Select(c => c.Id).ToList();
        var seedings = (await seedingRecords.LaneSeedingsOfCyclesAsync(cycleIds, cancellationToken)).ToLookup(s => s.CycleId);
        var harvests = (await harvestLots.LaneHarvestsOfCyclesAsync(cycleIds, cancellationToken)).ToLookup(h => h.CycleId);
        var steps = (await progress.StepsOfCyclesAsync(cycleIds, cancellationToken)).ToLookup(s => s.CycleId);
        return rafts.ToDictionary(
            r => r.Id,
            r => cycleOf.TryGetValue(r.Id, out var cycle)
                ? DayOf(r, date, cycle, seedings[cycle.Id].ToList(), harvests[cycle.Id].ToList(), steps[cycle.Id].ToList())
                : DayOf(r, date, null, [], [], []));
    }

    private static LaneDay DayOf(
        Raft raft, DateOnly date, CycleSpan? cycle, IReadOnlyList<LaneSeeding> seedings, IReadOnlyList<LaneHarvest> harvests, IReadOnlyList<LaneProgressStep> steps)
    {
        var laneIds = raft.Lanes.Select(l => l.Id).ToList();
        var states = LaneStateRules.StatesOn(date, cycle, laneIds, seedings, harvests, steps);
        return new LaneDay(
            cycle,
            LaneStateRules.WithOutOfService(states, raft.Lanes, date),
            LaneStateRules.LanesWithRecords(cycle, seedings, harvests),
            LaneStateRules.PercentsOn(date, cycle, laneIds, seedings, harvests, steps),
            steps);
    }
}
