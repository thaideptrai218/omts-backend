using OMTS.Domain.Common;
using OMTS.Domain.Enums;

namespace OMTS.Domain.Entities.Farming;

/// <summary>
/// One entry of the history of a lane (decision of 12/11/2026): reported damaged, repaired, or put out of service, with who, when and why. A damage report blocks seeding and harvest on the lane
/// and keeps its state; a repair clears it; out of service ends the lane for good and, when seed hung on it, is the loss event of that seed (no quantity, as the system has none per lane).
/// Entries are never changed or removed.
/// </summary>
public class LaneIncident : AuditableEntity
{
    public const int MaxReasonLength = 500;
    public const int MaxPhotos = 3;

    public Guid LaneId { get; set; }
    public Lane Lane { get; set; } = null!;

    /// <summary>The cycle of the raft that was Open at the time, or null when there was none.</summary>
    public Guid? CycleId { get; set; }

    public LaneIncidentKind Kind { get; set; }

    public DateTimeOffset OccurredAt { get; set; }

    /// <summary>The business date (UTC+7) of <see cref="OccurredAt"/>.</summary>
    public DateOnly OccurredOn { get; set; }

    /// <summary>Optional category: LineBroken, BuoyDamaged, Storm, MassMortality or Other.</summary>
    public string? ReasonCode { get; set; }

    /// <summary>Required: what happened (damage, out of service) or what was done (repair).</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>The state of the lane when it happened (NotSeeded, Growing, BeingHarvested, Harvested), as worked out then.</summary>
    public string StateBefore { get; set; } = string.Empty;

    /// <summary>The lane had seed in the Open cycle when it was put out of service: the loss event of that seed, with no quantity. Always false for the other kinds.</summary>
    public bool HadSeed { get; set; }

    public Guid RecordedByAccountId { get; set; }

    /// <summary>The damage report a repair resolves.</summary>
    public Guid? ResolvesIncidentId { get; set; }

    public ICollection<LaneIncidentPhoto> Photos { get; set; } = [];
    public ICollection<LaneIncidentMaterial> Materials { get; set; } = [];
}

/// <summary>An optional photo of an incident, at most <see cref="LaneIncident.MaxPhotos"/> per incident, one per slot.</summary>
public class LaneIncidentPhoto : AuditableEntity
{
    public Guid IncidentId { get; set; }
    public LaneIncident Incident { get; set; } = null!;

    public int SortOrder { get; set; }

    /// <summary>Storage key; reads use signed links.</summary>
    public string FileKey { get; set; } = string.Empty;
}

/// <summary>A material a repair took from the stock: the usage record of the raft (which carries its cost) and the use issue (Xuất dùng) it posted.</summary>
public class LaneIncidentMaterial : Entity
{
    public Guid IncidentId { get; set; }
    public LaneIncident Incident { get; set; } = null!;

    public Guid MaterialUsageId { get; set; }
    public Guid ItemId { get; set; }
    public decimal Quantity { get; set; }

    /// <summary>The use issue the usage posted.</summary>
    public Guid? IssueId { get; set; }
}
