using OMTS.Domain.Constants;

namespace OMTS.Application.Features.Harvest.Plans;

/// <summary>Why the caller may not choose the crew of a plan line now.</summary>
public enum StaffingRefusal
{
    None,

    /// <summary>The caller has no right to staff this line in this status (a Zone Manager on a Draft, a caller with no work assignment right in the zone).</summary>
    Forbidden,

    /// <summary>The plan is over or handed over: HandedOver, Completed or Cancelled.</summary>
    WrongStatus,
}

/// <summary>What happens to an assignment made for a plan line when the plan changes.</summary>
public enum StaffingFollow
{
    /// <summary>It stays as it is.</summary>
    Keep,

    /// <summary>It moves to the plan's new day.</summary>
    Move,

    /// <summary>It is removed: the line or its raft is gone, or the plan is cancelled, and no one checked in on it.</summary>
    Remove,

    /// <summary>It stays as the record of work done or past, but no longer belongs to the plan.</summary>
    Unlink,
}

/// <summary>The rules of the crew of a harvest plan (Nhân công) as pure functions, so they are unit tested and every endpoint gives one answer.</summary>
public static class HarvestPlanStaffingRules
{
    /// <summary>
    /// Who chooses the crew of a line: the Owner while the plan is a Draft or Assigned; the Zone Manager of the line's zone (any caller who may create work assignments there, the Owner included) once it is
    /// Assigned, Received or Harvesting; nobody once it is HandedOver, Completed or Cancelled.
    /// </summary>
    /// <param name="isOwner">The caller is the Cooperative Owner.</param>
    /// <param name="mayWorkInZone">The caller may create work assignments in the zone of the line (the Owner in every zone).</param>
    public static StaffingRefusal Refusal(int statusId, bool isOwner, bool mayWorkInZone) => statusId switch
    {
        StatusIds.HarvestPlan.Draft => isOwner ? StaffingRefusal.None : StaffingRefusal.Forbidden,
        StatusIds.HarvestPlan.Assigned or StatusIds.HarvestPlan.Received or StatusIds.HarvestPlan.Harvesting => isOwner || mayWorkInZone ? StaffingRefusal.None : StaffingRefusal.Forbidden,
        _ => StaffingRefusal.WrongStatus,
    };

    /// <summary>The crew is chosen only for a day that has not passed: work is assigned for today or later.</summary>
    public static bool DayOpen(DateOnly plannedDate, DateOnly today) => plannedDate >= today;

    /// <summary>
    /// What becomes of an assignment of a line that goes (a line taken out of the plan, its raft changed, the plan cancelled): it is removed when it is of today or later and no one checked in on it, and kept as
    /// the record of the work otherwise, without the link.
    /// </summary>
    public static StaffingFollow WhenLineGoes(DateOnly assignmentDate, DateOnly today, bool checkedIn) =>
        checkedIn || assignmentDate < today ? StaffingFollow.Unlink : StaffingFollow.Remove;

    /// <summary>What becomes of an assignment of a line that stays when the plan's day is <paramref name="planDate"/>: it moves with the plan unless the employee checked in on its day.</summary>
    public static StaffingFollow WhenDateChanges(DateOnly assignmentDate, DateOnly planDate, bool checkedIn) =>
        assignmentDate == planDate ? StaffingFollow.Keep : checkedIn ? StaffingFollow.Unlink : StaffingFollow.Move;
}
