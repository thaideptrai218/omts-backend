using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Farming.Rafts;

public enum PortionCondition
{
    NotSeeded,
    Growing,
    Harvested,
    WrittenOff,
}

public enum LaneSummary
{
    NotSeeded,
    Growing,
    PartiallyHarvested,
    FullyHarvested,
}

/// <summary>A recorded lane segment, in metres from marker A.</summary>
public sealed record SegmentFact(Guid Id, LaneSegmentType Type, decimal From, decimal To, DateOnly Date);

/// <summary>A stretch of the lane with one condition and the segments that make it so.</summary>
public sealed record LanePortion(decimal From, decimal To, PortionCondition Condition, IReadOnlyList<Guid> SegmentIds);

/// <summary>
/// FARM-04 lane condition: splits 0…length into portions. Seeded segments make a portion Growing;
/// Harvested or Written off segments override Growing; anything else is Not seeded. Planned harvests are
/// an overlay, never a condition, and no kg is inferred from lengths.
/// </summary>
public static class LaneConditionRules
{
    public static IReadOnlyList<LanePortion> Portions(decimal length, IReadOnlyCollection<SegmentFact> segments)
    {
        var relevant = segments
            .Where(s => s.Type != LaneSegmentType.PlannedHarvest && s.To > s.From)
            .Select(s => s with { From = Math.Max(0, s.From), To = Math.Min(length, s.To) })
            .Where(s => s.To > s.From)
            .ToList();

        // Every segment edge starts a new candidate portion.
        var cuts = relevant.SelectMany(s => new[] { s.From, s.To }).Append(0).Append(length).Distinct().Order().ToList();
        var portions = new List<LanePortion>();
        for (var i = 0; i < cuts.Count - 1; i++)
        {
            var (from, to) = (cuts[i], cuts[i + 1]);
            var covering = relevant.Where(s => s.From <= from && s.To >= to).ToList();
            var condition = ConditionOf(covering);
            var sources = covering.Select(s => s.Id).ToList();

            // Merge with the previous portion when nothing changes.
            if (portions.Count > 0 && portions[^1].Condition == condition && portions[^1].SegmentIds.SequenceEqual(sources))
            {
                portions[^1] = portions[^1] with { To = to };
            }
            else
            {
                portions.Add(new LanePortion(from, to, condition, sources));
            }
        }

        return portions;
    }

    /// <summary>Legend of the raft map for one lane.</summary>
    public static LaneSummary Summarize(IReadOnlyList<LanePortion> portions)
    {
        var growing = portions.Any(p => p.Condition == PortionCondition.Growing);
        var done = portions.Any(p => p.Condition is PortionCondition.Harvested or PortionCondition.WrittenOff);
        var harvested = portions.Any(p => p.Condition == PortionCondition.Harvested);
        return (growing, done) switch
        {
            (false, false) => LaneSummary.NotSeeded,
            (true, false) => LaneSummary.Growing,
            (true, true) => harvested ? LaneSummary.PartiallyHarvested : LaneSummary.Growing,
            (false, true) => LaneSummary.FullyHarvested,
        };
    }

    private static PortionCondition ConditionOf(IReadOnlyList<SegmentFact> covering)
    {
        // The latest harvest or write-off decides between the two when both cover the stretch.
        var finished = covering.Where(s => s.Type is LaneSegmentType.Harvested or LaneSegmentType.WrittenOff).MaxBy(s => s.Date);
        if (finished is not null)
        {
            return finished.Type == LaneSegmentType.Harvested ? PortionCondition.Harvested : PortionCondition.WrittenOff;
        }

        return covering.Any(s => s.Type == LaneSegmentType.Seeded) ? PortionCondition.Growing : PortionCondition.NotSeeded;
    }
}
