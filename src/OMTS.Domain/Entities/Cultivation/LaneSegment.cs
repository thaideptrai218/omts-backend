using OMTS.Domain.Common;
using OMTS.Domain.Enums;

namespace OMTS.Domain.Entities;

/// <summary>
/// E18 – A lane portion measured from markers A/B: seeded, planned harvest, harvested or written off.
/// Each segment only fills the foreign keys matching its <see cref="SegmentType"/>; segments of the
/// same type never overlap within a cycle (BR-03). No weighed kg is stored here (BR-10).
/// </summary>
public class LaneSegment : AuditableEntity
{
    public Guid CycleId { get; set; }
    public CultivationCycle Cycle { get; set; } = null!;

    public Guid LaneId { get; set; }
    public Lane Lane { get; set; } = null!;

    public LaneSegmentType SegmentType { get; set; }

    /// <summary>Distance in metres from marker A.</summary>
    public decimal FromPosition { get; set; }
    public decimal ToPosition { get; set; }

    public DateOnly SegmentDate { get; set; }
    public Guid RecordedByAccountId { get; set; }
    public Guid? ConfirmedByAccountId { get; set; }

    /// <summary>Seeded segment.</summary>
    public Guid? SeedingId { get; set; }
    public SeedingEvent? Seeding { get; set; }

    /// <summary>Planned harvest segment.</summary>
    public Guid? OrderId { get; set; }
    public SalesOrder? Order { get; set; }
    public decimal? PlannedKg { get; set; }

    /// <summary>Harvested segment; linked to at most one lot.</summary>
    public Guid? HarvestEventId { get; set; }
    public HarvestEvent? HarvestEvent { get; set; }
    public Guid? LotId { get; set; }
    public TraceabilityLot? Lot { get; set; }
    public string? PlanDeviationReason { get; set; }

    /// <summary>Written-off segment (created by a “Write off lane segment” log entry).</summary>
    public Guid? LogId { get; set; }
    public CultivationLogEntry? Log { get; set; }
    public string? WriteOffReason { get; set; }
    public string? WriteOffPhotoUrl { get; set; }
}
