using OMTS.Domain.Common;
using OMTS.Domain.Statuses;

namespace OMTS.Domain.Entities;

/// <summary>
/// E16 – One farming round on a raft. Closes only when every seeded lane segment has been harvested
/// or written off (BR-21).
/// </summary>
public class CultivationCycle : AuditableEntity, IHasStatus
{
    public Guid RaftId { get; set; }
    public Raft Raft { get; set; } = null!;

    public Guid? PlannedSeedTypeId { get; set; }
    public OysterSeedType? PlannedSeedType { get; set; }

    public DateTimeOffset OpenedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }

    public DateTimeOffset? SeedingCompletedAt { get; set; }
    public string? SeedingCompletionReason { get; set; }

    public string? ClosingSummary { get; set; }

    public int StatusId { get; set; } = StatusIds.CultivationCycle.Open;
    public Status Status { get; set; } = null!;

    public ICollection<SeedingEvent> SeedingEvents { get; set; } = [];
    public ICollection<LaneSegment> LaneSegments { get; set; } = [];
    public ICollection<CultivationLogEntry> LogEntries { get; set; } = [];
}
