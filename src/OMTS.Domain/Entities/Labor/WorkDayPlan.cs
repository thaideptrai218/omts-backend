using OMTS.Domain.Common;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Domain.Entities.Labor;

/// <summary>
/// Kế hoạch ngày làm (KNL000001): the people who do one kind of field work in one place on one day. A harvest plan is made with its line of a harvest plan; nursery and seeding plans are made
/// by the Owner or the Zone Manager of the zone. It is Ready only when the confirmed members plus the people hired outside reach <see cref="RequiredPeople"/>; a plan short of people cannot run.
/// A member who is confirmed has a <see cref="WorkAssignment"/> on the plan's day and place with the job type of the kind.
/// </summary>
public class WorkDayPlan : AuditableEntity
{
    /// <summary>KNL000123, or a code the user typed; unique regardless of letter case.</summary>
    public string Code { get; set; } = string.Empty;

    public WorkDayPlanKind Kind { get; set; }
    public WorkDayPlanStatus Status { get; set; } = WorkDayPlanStatus.Staffing;

    public DateOnly WorkDate { get; set; }
    public DateTimeOffset? StartsAt { get; set; }
    public DateTimeOffset? ExpectedEndAt { get; set; }

    /// <summary>The zone of the work: the zone of the raft, or the zone chosen for a nursery plan.</summary>
    public Guid ZoneId { get; set; }
    public FarmingZone Zone { get; set; } = null!;

    /// <summary>The raft of a harvest or seeding plan; empty for a nursery plan.</summary>
    public Guid? RaftId { get; set; }
    public Raft? Raft { get; set; }

    /// <summary>The nursery batch a nursery care day is for.</summary>
    public Guid? NurseryBatchId { get; set; }
    public NurseryBatch? NurseryBatch { get; set; }

    /// <summary>The line of the harvest plan this plan was made with (a harvest plan only); it follows the line and the plan is cancelled when the line goes.</summary>
    public Guid? HarvestPlanLineId { get; set; }
    public HarvestPlanLine? HarvestPlanLine { get; set; }

    public Guid CreatorAccountId { get; set; }
    public Account CreatorAccount { get; set; } = null!;

    /// <summary>The person in charge. Does not count towards <see cref="RequiredPeople"/>.</summary>
    public Guid ResponsibleAccountId { get; set; }
    public Account ResponsibleAccount { get; set; } = null!;

    /// <summary>How many people the work needs, 1 to 500, not counting the person in charge.</summary>
    public int RequiredPeople { get; set; } = 1;

    public string? Note { get; set; }

    /// <summary>Makes a plan sent twice by the mobile app one plan.</summary>
    public Guid ClientOperationId { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public string? CancelReason { get; set; }

    public ICollection<WorkDayPlanMember> Members { get; set; } = [];
    public ICollection<PlanOutsiderHire> OutsiderHires { get; set; } = [];

    // The fields below belong to the older plan of one raft and one job type that the person in charge had to accept before assigning; they stay until the old endpoints go.

    /// <summary>The job type chosen on the old form; a new plan takes its job type from <see cref="Kind"/>.</summary>
    public Guid? JobTypeId { get; set; }
    public JobType? JobType { get; set; }
    public Guid? PurchaseRequestId { get; set; }
    public PurchaseRequest? PurchaseRequest { get; set; }
    public bool? Approved { get; set; }
    public DateTimeOffset? RespondedAt { get; set; }
    public string? ResponseReason { get; set; }
}
