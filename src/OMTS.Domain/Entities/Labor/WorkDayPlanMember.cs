using OMTS.Domain.Common;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;

namespace OMTS.Domain.Entities.Labor;

/// <summary>One person of a work-day plan and their answer. One row per person per plan.</summary>
public class WorkDayPlanMember : AuditableEntity
{
    public Guid PlanId { get; set; }
    public WorkDayPlan Plan { get; set; } = null!;

    public Guid EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;

    public WorkDayMemberState State { get; set; } = WorkDayMemberState.Invited;

    public DateTimeOffset InvitedAt { get; set; }

    /// <summary>The time to answer; null for a person put on the plan already confirmed.</summary>
    public DateTimeOffset? Deadline { get; set; }

    /// <summary>When the person answered, was confirmed for, or the invitation ended.</summary>
    public DateTimeOffset? RespondedAt { get; set; }

    /// <summary>Required for <see cref="WorkDayMemberState.ConfirmedOnBehalf"/>; optional for a decline.</summary>
    public string? Reason { get; set; }

    public Guid? AddedByAccountId { get; set; }
    public Guid? ConfirmedByAccountId { get; set; }

    /// <summary>
    /// The assignment of the day a confirmed member has; null while the member is not confirmed. Not a foreign key: the older assignment screens can still remove an assignment, and the member stays on the plan.
    /// </summary>
    public Guid? WorkAssignmentId { get; set; }

    /// <summary>The member counts towards the people needed.</summary>
    public bool IsConfirmed => State is WorkDayMemberState.Accepted or WorkDayMemberState.ConfirmedOnBehalf;
}
