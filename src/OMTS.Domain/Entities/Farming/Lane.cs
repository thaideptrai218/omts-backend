using OMTS.Domain.Common;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Platform;

namespace OMTS.Domain.Entities.Farming;

/// <summary>
/// The space between two main lines of a raft, numbered 1 to N. The number never changes and a lane is never deleted: a
/// lane taken off the end of the raft is Removed and keeps its records (BR-FARM-04). Its state on a date (Not seeded,
/// Growing, Being harvested, Harvested) is worked out from the seeding sessions and harvest lots, never stored (BR-FARM-03).
/// </summary>
public class Lane : AuditableEntity, IHasStatus
{
    public Guid RaftId { get; set; }
    public Raft Raft { get; set; } = null!;

    public int LaneNumber { get; set; }

    public int StatusId { get; set; } = StatusIds.Lane.InUse;
    public Status Status { get; set; } = null!;
}
