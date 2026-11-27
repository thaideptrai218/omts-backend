using OMTS.Application.Features.Cultivation.Progress;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Application.Features.Farming.Rafts;

/// <summary>
/// The lanes In Use of a raft counted by what they are today, as a pure function of the lanes and their states (<see cref="LaneStateRules"/>). A lane counts once: out of service first,
/// then damaged (both are physical facts that win over the records, as on the lane screens), else by its state, Growing and Being harvested together as farming.
/// </summary>
public static class RaftLaneSummaryRules
{
    /// <summary>Null when the raft has no lane In Use (a Removed lane is no longer part of the raft). A lane with no state in <paramref name="states"/> is Not seeded.</summary>
    public static RaftLaneSummary? Of(IEnumerable<Lane> lanes, IReadOnlyDictionary<Guid, LaneState> states)
    {
        int farming = 0, notSeeded = 0, harvested = 0, damaged = 0, outOfService = 0;
        var any = false;
        foreach (var lane in lanes.Where(l => l.StatusId == StatusIds.Lane.InUse))
        {
            any = true;
            if (lane.IsOutOfService)
            {
                outOfService++;
            }
            else if (lane.IsDamaged)
            {
                damaged++;
            }
            else
            {
                switch (states.GetValueOrDefault(lane.Id, LaneState.NotSeeded))
                {
                    case LaneState.Growing or LaneState.BeingHarvested:
                        farming++;
                        break;
                    case LaneState.Harvested:
                        harvested++;
                        break;
                    case LaneState.OutOfService:
                        outOfService++;
                        break;
                    default:
                        notSeeded++;
                        break;
                }
            }
        }

        return any ? new RaftLaneSummary(farming, notSeeded, harvested, damaged, outOfService) : null;
    }
}
