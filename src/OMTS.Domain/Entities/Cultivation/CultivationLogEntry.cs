using OMTS.Domain.Common;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Domain.Entities.Cultivation;

/// <summary>
/// E19 – Cultivation event/observation that is not a structured water measurement. Kept only because a
/// lane with an entry cannot be removed from a raft (FARM-04); the SRS 27/10 CultivationLog replaces it.
/// </summary>
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
