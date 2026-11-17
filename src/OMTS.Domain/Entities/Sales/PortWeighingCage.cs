using OMTS.Domain.Common;
using OMTS.Domain.Entities.Harvest;

namespace OMTS.Domain.Entities.Sales;

/// <summary>One cage of a <see cref="PortWeighing"/>, from one harvest lot.</summary>
public class PortWeighingCage : Entity
{
    public Guid PortWeighingId { get; set; }
    public PortWeighing PortWeighing { get; set; } = null!;

    /// <summary>1, 2, … in the order weighed.</summary>
    public int CageNumber { get; set; }

    public Guid HarvestLotId { get; set; }
    public HarvestLot HarvestLot { get; set; } = null!;

    /// <summary>Above 0, at most 3 decimals.</summary>
    public decimal Kg { get; set; }
}
