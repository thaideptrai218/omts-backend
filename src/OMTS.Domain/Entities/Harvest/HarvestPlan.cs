using OMTS.Domain.Common;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Domain.Entities.Harvest;

/// <summary>
/// A harvest plan (Kế hoạch thu hoạch, KHT000001) the Owner makes for a planned time. Its lines name a raft (of any zone: the zone of a line is the zone of its raft) and the cages expected; a line is one whole raft and a plan holds no lanes, because the lanes are known only at sea and are recorded on the
/// harvest lot. The product is that of the raft's cultivation cycle. It is a Draft until handed to the Zone Managers of every zone of its lines (Assigned), Received when a Zone Manager takes it, Harvesting with the first lot recorded from one of its lines,
/// HandedOver when a Zone Manager reports every line done, and Completed when the Owner finishes it. It may serve several orders. A lot recorded from a line carries a link to it; the lot code does not change.
/// </summary>
public class HarvestPlan : AuditableEntity, IHasStatus
{
    /// <summary>KHT000123: the user may type another code, unique regardless of letter case, drafts and cancelled plans included.</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>When the harvest is planned, at UTC+7.</summary>
    public DateTimeOffset PlannedAt { get; set; }

    /// <summary>The business date (UTC+7) of <see cref="PlannedAt"/>, for period filters.</summary>
    public DateOnly PlannedDate { get; set; }

    public string? Note { get; set; }

    public Guid CreatedByAccountId { get; set; }

    /// <summary>When and by whom the plan was handed to the Zone Managers of its zones; null while it is a Draft (and for a Draft that was cancelled).</summary>
    public DateTimeOffset? AssignedAt { get; set; }
    public Guid? AssignedByAccountId { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }
    public Guid? CompletedByAccountId { get; set; }

    public DateTimeOffset? CancelledAt { get; set; }
    public Guid? CancelledByAccountId { get; set; }
    public string? CancelReason { get; set; }

    public int StatusId { get; set; } = StatusIds.HarvestPlan.Draft;
    public Status Status { get; set; } = null!;

    public ICollection<HarvestPlanLine> Lines { get; set; } = [];

    /// <summary>The orders the plan serves.</summary>
    public ICollection<SalesOrderHarvestPlan> Orders { get; set; } = [];
}

/// <summary>
/// One whole raft of a plan, with the cages expected. Its zone is the zone of the raft. It is done when its lot is recorded. One line makes one lot: a later trip to the same raft is a lot outside the plan.
/// </summary>
public class HarvestPlanLine : Entity
{
    public Guid PlanId { get; set; }
    public HarvestPlan Plan { get; set; } = null!;

    /// <summary>1, 2, … in the order entered.</summary>
    public int LineNumber { get; set; }

    public Guid RaftId { get; set; }
    public Raft Raft { get; set; } = null!;

    /// <summary>The product the raft's cultivation cycle farms, as it was when the line was saved; null when the cycle has no harvest with products yet.</summary>
    public Guid? ExpectedProductId { get; set; }
    public Product? ExpectedProduct { get; set; }

    public decimal? ExpectedCageCount { get; set; }

    public string? Note { get; set; }

    /// <summary>The lot recorded from this line (it holds the link, <see cref="HarvestLot.HarvestPlanLineId"/>); null until the Zone Manager records it, and again when that lot is removed.</summary>
    public HarvestLot? Lot { get; set; }
}
