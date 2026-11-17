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

    /// <summary>Length of this lane's lines in metres when it differs from the raft's; null means the lane takes the length of the raft. At most the raft's length.</summary>
    public decimal? LineLengthM { get; set; }

    /// <summary>
    /// The lane is damaged (a line snapped, a buoy lost) and waits for a repair: no seeding and no harvest on it. It keeps the state it had, which is worked out from the records as always;
    /// the repair clears the flag, so the lane is exactly as before. The history is in <see cref="LaneIncident"/>.
    /// </summary>
    public bool IsDamaged { get; set; }

    /// <summary>
    /// The day the lane was put out of service, when it was too damaged to repair: from then on it is left out of every list of lanes to seed or harvest and its number is never used
    /// again. Null while in service. Out of service is one way: records and history stay.
    /// </summary>
    public DateOnly? OutOfServiceOn { get; set; }

    public bool IsOutOfService => OutOfServiceOn is not null;

    /// <summary>True when the lane can take new seed or a new harvest as far as its own flags go; its state is a separate question.</summary>
    public bool IsUsable => StatusId == StatusIds.Lane.InUse && !IsDamaged && !IsOutOfService;
}
