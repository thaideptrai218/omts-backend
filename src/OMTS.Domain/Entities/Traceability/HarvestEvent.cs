using OMTS.Domain.Common;
using OMTS.Domain.Statuses;

namespace OMTS.Domain.Entities;

/// <summary>
/// E24 – Actual harvest of a day for an order. Kg at the raft is only an estimate, never a commercial
/// weighing (BR-11); the harvested lane portions are its LaneSegments.
/// </summary>
public class HarvestEvent : AuditableEntity, IHasStatus
{
    public Guid OrderId { get; set; }
    public SalesOrder Order { get; set; } = null!;

    public Guid ZoneId { get; set; }
    public FarmingZone Zone { get; set; } = null!;

    public DateOnly HarvestDate { get; set; }
    public decimal? EstimatedKg { get; set; }
    public string? LotPhotoUrl { get; set; }
    public Guid RecordedByAccountId { get; set; }

    public int StatusId { get; set; } = StatusIds.HarvestEvent.Draft;
    public Status Status { get; set; } = null!;

    public ICollection<LaneSegment> HarvestedSegments { get; set; } = [];
}
