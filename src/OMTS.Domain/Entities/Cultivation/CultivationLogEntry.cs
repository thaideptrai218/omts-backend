using OMTS.Domain.Common;

namespace OMTS.Domain.Entities;

/// <summary>E19 – Cultivation event/observation that is not a structured water measurement.</summary>
public class CultivationLogEntry : AuditableEntity
{
    public Guid ZoneId { get; set; }
    public FarmingZone Zone { get; set; } = null!;

    public Guid? CycleId { get; set; }
    public CultivationCycle? Cycle { get; set; }

    public Guid? LaneId { get; set; }
    public Lane? Lane { get; set; }

    public DateTimeOffset OccurredAt { get; set; }
    public string? LanePosition { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string? Observation { get; set; }
    public string? Observer { get; set; }
    public Guid RecordedByAccountId { get; set; }
    public List<string> EvidenceUrls { get; set; } = [];
}
