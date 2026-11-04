using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Common;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Platform;

namespace OMTS.Domain.Entities.Cultivation;

/// <summary>E16 – One farming round on a raft; an Open cycle blocks lane changes and discontinuing the raft (FARM-04).</summary>
public class CultivationCycle : AuditableEntity, IHasStatus
{
    public Guid RaftId { get; set; }
    public Raft Raft { get; set; } = null!;

    public Guid? PlannedSeedTypeId { get; set; }
    public FarmingInputItem? PlannedSeedType { get; set; }

    public DateTimeOffset OpenedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }

    public DateTimeOffset? SeedingCompletedAt { get; set; }
    public string? SeedingCompletionReason { get; set; }

    public string? ClosingSummary { get; set; }

    public int StatusId { get; set; } = StatusIds.CultivationCycle.Open;
    public Status Status { get; set; } = null!;

    public ICollection<SeedingEvent> SeedingEvents { get; set; } = [];
}
