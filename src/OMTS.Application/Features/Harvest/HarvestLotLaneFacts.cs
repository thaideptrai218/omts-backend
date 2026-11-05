using OMTS.Application.Features.Cultivation.Progress;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Harvest;

namespace OMTS.Application.Features.Harvest;

/// <summary>A lane in a lot of a cycle, with the lot and its time.</summary>
public sealed record LotLaneFact(Guid LotId, Guid LaneId, DateOnly Date, DateTimeOffset HarvestedAt, bool IsFinished);

/// <summary>
/// The lanes as one lot of an Open cycle sees them, for its correction or removal (HARVEST-01.1, BR-FARM-03): the states are worked out without the lot's own part, as
/// the records of the other lots still stand. Read after the cycle row, so the save fails with MSG06 if anything changed the cycle since. A pure class, so the rules can be read in one place.
/// </summary>
internal sealed class HarvestLotLaneFacts(
    Raft raft, CycleSpan cycle, HarvestLot lot, IReadOnlyCollection<LaneSeeding> seedings, IReadOnlyCollection<LotLaneFact> facts, DateOnly today)
{
    private readonly List<LotLaneFact> others = facts.Where(f => f.LotId != lot.Id).ToList();

    /// <summary>
    /// What each lane counts as for the field check. The lanes of the lot stay allowed, also one that a later lot has finished: only what the correction adds is a new choice. A lane
    /// the correction adds must be Growing or Being harvested without this lot (BR-HARVEST-01), and must have had seed on the harvest date, which never changes (BR-HARVEST-03).
    /// </summary>
    public IReadOnlyDictionary<Guid, LaneState> Choosable()
    {
        var own = lot.Lanes.Select(l => l.LaneId).ToHashSet();
        var seededByThen = seedings.Where(s => s.Date <= lot.HarvestDate).Select(s => s.LaneId).ToHashSet();
        return States(others).ToDictionary(
            state => state.Key,
            state => own.Contains(state.Key) ? LaneState.BeingHarvested
                : LaneStateRules.CanBeHarvested(state.Value) && !seededByThen.Contains(state.Key) ? LaneState.NotSeeded
                : state.Value);
    }

    /// <summary>
    /// A lane marked finished has no oysters left, so no later lot can hold it (BR-HARVEST-01; design question 10, rejected). Null when the finished lanes are fine, else why not.
    /// A lane the lot already marks finished is no new claim, so it never refuses a correction.
    /// </summary>
    public string? FinishedHint(IReadOnlyList<int>? finishedNumbers)
    {
        var inALaterLot = others.Where(f => f.HarvestedAt > lot.HarvestedAt).Select(f => f.LaneId).ToHashSet();
        var alreadyFinished = lot.Lanes.Where(l => l.IsFinished).Select(l => l.LaneId).ToHashSet();
        var refused = raft.Lanes
            .Where(l => (finishedNumbers ?? []).Contains(l.LaneNumber) && inALaterLot.Contains(l.Id) && !alreadyFinished.Contains(l.Id))
            .Select(l => l.LaneNumber).Order().ToList();
        return refused.Count == 0 ? null : $"Lanes {string.Join(", ", refused)} are in a later harvest lot and cannot be marked finished in this one.";
    }

    /// <summary>Every seeded lane shows Harvested with the lot as saved (BR-CYCLE-07).</summary>
    public bool EveryHarvestedNow() => EveryHarvested(facts);

    /// <summary>The same with the corrected lanes of the lot.</summary>
    public bool EveryHarvestedWith(HarvestLotFields fields) => EveryHarvested(Corrected(fields));

    /// <summary>The removal takes a lane out of Harvested, whatever the other lanes show: a lane seeded later may be Growing (client list, section 4).</summary>
    public bool RemovalTakesALaneOutOfHarvested() => TakesALaneOutOfHarvested(others);

    /// <summary>The same for the corrected lanes of the lot.</summary>
    public bool CorrectionTakesALaneOutOfHarvested(HarvestLotFields fields) => TakesALaneOutOfHarvested(Corrected(fields));

    private List<LotLaneFact> Corrected(HarvestLotFields fields) =>
        [.. others, .. fields.Lanes.Select(l => new LotLaneFact(lot.Id, l.Id, lot.HarvestDate, lot.HarvestedAt, fields.FinishedLaneIds.Contains(l.Id)))];

    private bool EveryHarvested(IReadOnlyCollection<LotLaneFact> lots)
    {
        var states = States(lots);
        return LaneStateRules.EverySeededLaneHarvested(LanesInUse().Select(l => states[l.Id]));
    }

    private bool TakesALaneOutOfHarvested(IReadOnlyCollection<LotLaneFact> lots)
    {
        var (now, then) = (States(facts), States(lots));
        return LanesInUse().Any(l => now[l.Id] == LaneState.Harvested && then[l.Id] != LaneState.Harvested);
    }

    private IEnumerable<Lane> LanesInUse() => raft.Lanes.Where(l => l.StatusId == StatusIds.Lane.InUse);

    private IReadOnlyDictionary<Guid, LaneState> States(IReadOnlyCollection<LotLaneFact> lots) => LaneStateRules.StatesOn(
        today, cycle, raft.Lanes.Select(l => l.Id), seedings, lots.Select(f => new LaneHarvest(cycle.Id, f.LaneId, f.Date, f.IsFinished)).ToList());
}
