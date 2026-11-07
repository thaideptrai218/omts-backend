using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Application.Features.Farming.Rafts;

/// <summary>
/// BR-FARM-04: what a new number of lanes does to a raft. Lanes change only at the end of the raft: adding first returns the
/// Removed lanes in number order, then creates new numbers; removing marks the last lanes In Use as Removed. A number never
/// changes and a lane is never deleted.
/// </summary>
public sealed record RaftLaneChange(IReadOnlyList<Lane> Restored, IReadOnlyList<int> NewNumbers, IReadOnlyList<Lane> Removed)
{
    /// <summary>The most lanes a raft may have In Use; keeps a typing error from creating thousands of lanes.</summary>
    public const int MaxLaneCount = 200;

    public static RaftLaneChange To(int laneCount, IEnumerable<Lane> lanes)
    {
        var ordered = lanes.OrderBy(l => l.LaneNumber).ToList();
        var inUse = ordered.Where(l => l.StatusId == StatusIds.Lane.InUse).ToList();
        if (laneCount <= inUse.Count)
        {
            return new RaftLaneChange([], [], inUse.Skip(laneCount).ToList());
        }

        var needed = laneCount - inUse.Count;
        var restored = ordered.Where(l => l.StatusId == StatusIds.Lane.Removed).Take(needed).ToList();
        var last = ordered.Count == 0 ? 0 : ordered[^1].LaneNumber;
        return new RaftLaneChange(restored, Enumerable.Range(last + 1, needed - restored.Count).ToList(), []);
    }

    /// <summary>The number of lanes In Use of a raft.</summary>
    public static int InUseCount(IEnumerable<Lane> lanes) => lanes.Count(l => l.StatusId == StatusIds.Lane.InUse);
}
