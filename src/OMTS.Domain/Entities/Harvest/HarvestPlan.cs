using OMTS.Domain.Common;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Domain.Entities.Harvest;

/// <summary>
/// A harvest plan (Kế hoạch thu hoạch, KHT000001) the Owner makes for a planned time. Its lines name a raft (of any zone: the zone of a line is the zone of its raft), the lanes to harvest and the cages expected; the
/// product is that of the raft's cultivation cycle. It is a Draft until handed to the Zone Managers of every zone of its lines (Assigned), becomes Harvesting with the first lot recorded from one of its lines or the first lane left out, and Completed when every line has its lot or every lane left out, or
/// when the Owner finishes it. It may serve several orders. A lot recorded from a line carries a link to it; the lot code does not change.
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

    /// <summary>Who harvests the plan, set when it is assigned; <see cref="HarvestPlanExecutor.ZoneManager"/> until then and for every plan made before the choice existed.</summary>
    public HarvestPlanExecutor Executor { get; set; } = HarvestPlanExecutor.ZoneManager;

    /// <summary>
    /// How many people the Owner needs to harvest the plan (1 to 500, from experience); null until set. Editable until the plan is Completed or Cancelled. Not enforced: starting short is allowed, the plan view only says it is not ready.
    /// </summary>
    public int? RequiredPeople { get; set; }

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

    /// <summary>The people chosen to harvest the plan and their answers.</summary>
    public ICollection<HarvestPlanStaffMember> Staff { get; set; } = [];
}

/// <summary>
/// One raft of a plan, with the lanes to harvest and the cages expected. Its zone is the zone of the raft. It is done when its lot is recorded or every lane is left out. One line makes one lot: a later trip to the same lanes is a lot outside the plan.
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

    public ICollection<HarvestPlanLane> Lanes { get; set; } = [];

    /// <summary>The lot recorded from this line (it holds the link, <see cref="HarvestLot.HarvestPlanLineId"/>); null until the Zone Manager records it, and again when that lot is removed.</summary>
    public HarvestLot? Lot { get; set; }

    /// <summary>The work-day plan of the people who harvest this line (one per line); null until it is made.</summary>
    public Labor.WorkDayPlan? WorkDayPlan { get; set; }
}

/// <summary>A lane planned on a line. It is harvested when it is among the lanes of the line's lot, and left out ("Không thu") when <see cref="SkippedAt"/> is set.</summary>
public class HarvestPlanLane
{
    public Guid LineId { get; set; }
    public HarvestPlanLine Line { get; set; } = null!;

    public Guid LaneId { get; set; }
    public Lane Lane { get; set; } = null!;

    public DateTimeOffset? SkippedAt { get; set; }
    public Guid? SkippedByAccountId { get; set; }

    /// <summary>Why the lane is not harvested; set with <see cref="SkippedAt"/>.</summary>
    public string? SkipReason { get; set; }

    public bool IsSkipped => SkippedAt is not null;
}

/// <summary>
/// A person chosen to harvest a plan (the staffing roster of the plan): an internal employee (Staff) or a seasonal worker, invited and waiting for the answer in the mobile app, or added outside the system on the spot
/// ("Bổ sung nhân công": a seasonal worker record is made for them and the row is <see cref="HarvestPlanStaffState.External"/>). One row per person per plan.
/// </summary>
public class HarvestPlanStaffMember : AuditableEntity
{
    public Guid PlanId { get; set; }
    public HarvestPlan Plan { get; set; } = null!;

    public Guid EmployeeId { get; set; }
    public Identity.Employee Employee { get; set; } = null!;

    public HarvestPlanStaffState State { get; set; } = HarvestPlanStaffState.Invited;

    /// <summary>When the person was put on the roster.</summary>
    public DateTimeOffset InvitedAt { get; set; }

    /// <summary>When the employee answered, or the Owner confirmed for them; null while Invited.</summary>
    public DateTimeOffset? RespondedAt { get; set; }

    /// <summary>Required for ConfirmedManually (why the Owner confirmed instead of the employee); optional for Declined.</summary>
    public string? Reason { get; set; }

    /// <summary>The account that put the person on the roster.</summary>
    public Guid? AddedByAccountId { get; set; }

    /// <summary>For ConfirmedManually: the account that confirmed.</summary>
    public Guid? ConfirmedByAccountId { get; set; }

    /// <summary>The person counts towards the people needed.</summary>
    public bool CountsAsPresent => State is HarvestPlanStaffState.Confirmed or HarvestPlanStaffState.ConfirmedManually or HarvestPlanStaffState.External;
}
