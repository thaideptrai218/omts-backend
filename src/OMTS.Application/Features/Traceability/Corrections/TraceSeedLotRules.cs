using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Domain.Entities.Harvest;

namespace OMTS.Application.Features.Traceability.Corrections;

/// <summary>A lane of a harvest lot.</summary>
public sealed record LotLane(Guid LotId, string LotCode, DateTimeOffset HarvestedAt, Guid LaneId, int LaneNumber);

/// <summary>A lane of a harvest lot that a correction would leave with no seed lot (MSG-TRACE-03).</summary>
public sealed record LaneWithoutSeed(string LotCode, int LaneNumber);

/// <summary>BR-TRACE-01 and the origin information of BR-ORIGIN-03 as pure functions, so TRACE-03 step 5 and the affected lots of step 6 are unit tested.</summary>
public static class TraceSeedLotRules
{
    public static IReadOnlyList<LotLane> LanesOf(IEnumerable<HarvestLot> lots) =>
        lots.SelectMany(lot => lot.Lanes.Select(l => new LotLane(lot.Id, lot.LotCode, lot.HarvestedAt, l.LaneId, l.Lane.LaneNumber))).ToList();

    /// <summary>
    /// TRACE-03.0.E3: the lanes of the lots of the cycle that have a seeding session before the correction and would have none after it, the earliest lot first. A lane that had
    /// none before is not the correction's doing, so it does not refuse it ("still has a seed lot"); the Trace lot screen lists it as missing (TRACE-02.1).
    /// </summary>
    /// <param name="seededBefore">The lanes with a seeding session of the cycle as the records stand.</param>
    /// <param name="seededAfter">The same with the correction.</param>
    public static IReadOnlyList<LaneWithoutSeed> LanesLosingTheirSeed(
        IReadOnlyCollection<LotLane> before, IReadOnlySet<Guid> seededBefore, IReadOnlyCollection<LotLane> after, IReadOnlySet<Guid> seededAfter)
    {
        var missingBefore = before.Where(l => !seededBefore.Contains(l.LaneId)).Select(l => (l.LotId, l.LaneId)).ToHashSet();
        return after
            .Where(l => !seededAfter.Contains(l.LaneId) && !missingBefore.Contains((l.LotId, l.LaneId)))
            .OrderBy(l => l.HarvestedAt).ThenBy(l => l.LaneNumber)
            .Select(l => new LaneWithoutSeed(l.LotCode, l.LaneNumber))
            .ToList();
    }

    /// <summary>
    /// The lanes whose origin information a seeding correction changes. The provenance page shows the seeding dates of the lanes and the seed item of each seed lot (BR-ORIGIN-03),
    /// so a new date or seed lot changes every lane of the session, before and after; a change of lanes changes the lanes taken out or added; the quantity alone changes none.
    /// </summary>
    public static IReadOnlySet<Guid> LanesWhoseOriginChanges(IReadOnlySet<Guid> lanesBefore, IReadOnlySet<Guid> lanesAfter, bool dateOrSeedLotChanged)
    {
        var changed = new HashSet<Guid>(lanesBefore);
        if (dateOrSeedLotChanged)
        {
            changed.UnionWith(lanesAfter);
        }
        else
        {
            changed.SymmetricExceptWith(lanesAfter);
        }

        return changed;
    }

    /// <summary>The lots holding one of <paramref name="lanes"/>, the earliest harvest first.</summary>
    public static IReadOnlyList<HarvestLot> LotsHolding(IEnumerable<HarvestLot> lots, IReadOnlySet<Guid> lanes) =>
        lots.Where(lot => lot.Lanes.Any(l => lanes.Contains(l.LaneId))).OrderBy(lot => lot.HarvestedAt).ThenBy(lot => lot.LotCode).ToList();

    /// <summary>MSG-TRACE-03 (400): the text names the first lane; <c>lanes</c> lists every lane and lot (TRACE-03.0.E3 names them all). Nothing is saved.</summary>
    public static AppError Refusal(IReadOnlyList<LaneWithoutSeed> lanes) => new(
        ErrorKind.Validation,
        UserMessages.LaneWithoutSeedLot(lanes[0].LaneNumber, lanes[0].LotCode),
        new Dictionary<string, object?> { ["field"] = "Lanes", ["lanes"] = lanes });
}
