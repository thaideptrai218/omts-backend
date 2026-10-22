using OMTS.Domain.Common;
using OMTS.Domain.Statuses;

namespace OMTS.Domain.Entities;

/// <summary>
/// E10 – Physical structure grouping lanes. Float and line counts are not stored here: they come from
/// the Raft Creation material record (BR-28).
/// </summary>
public class Raft : AuditableEntity, IHasStatus
{
    public Guid ZoneId { get; set; }
    public FarmingZone Zone { get; set; } = null!;

    /// <summary>Unique within the zone.</summary>
    public string Code { get; set; } = string.Empty;

    public string? Location { get; set; }
    public string? Size { get; set; }
    public string? PhotoUrl { get; set; }
    public string? ConditionNotes { get; set; }

    public int StatusId { get; set; } = StatusIds.Usage.InUse;
    public Status Status { get; set; } = null!;

    public ICollection<Lane> Lanes { get; set; } = [];
    public ICollection<RaftLayoutVersion> LayoutVersions { get; set; } = [];
    public ICollection<CultivationCycle> CultivationCycles { get; set; } = [];
    public ICollection<RaftMaterialRecord> MaterialRecords { get; set; } = [];
}
