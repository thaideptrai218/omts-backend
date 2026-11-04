using OMTS.Domain.Entities.Farming;

namespace OMTS.Domain.Entities.Harvest;

/// <summary>A whole lane in a harvest lot (BR-HARVEST-01). A lane harvested over several days is in several lots, until one marks it finished.</summary>
public class HarvestLotLane
{
    public Guid LotId { get; set; }
    public HarvestLot Lot { get; set; } = null!;

    public Guid LaneId { get; set; }
    public Lane Lane { get; set; } = null!;

    /// <summary>The Zone Manager marked the lane finished in this lot: no oysters are left to harvest on it (BR-FARM-03).</summary>
    public bool IsFinished { get; set; }
}
