using OMTS.Application.Features.Workforce;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.WorkDayPlans;

/// <summary>What changed when a plan was settled: its status before and after, and the invitations that ended because the plan became full.</summary>
public sealed record WorkDayPlanSettlement(WorkDayPlanStatus From, WorkDayPlanStatus To, IReadOnlyList<WorkDayPlanMember> Unsuccessful)
{
    public bool BecameReady => From == WorkDayPlanStatus.Staffing && To == WorkDayPlanStatus.Ready;
    public bool BecameShort => From == WorkDayPlanStatus.Ready && To == WorkDayPlanStatus.Staffing;
}

/// <summary>The answer to an employee who replied to an invitation.</summary>
public enum WorkDayAnswer
{
    /// <summary>The answer was taken: Accepted or Declined.</summary>
    Taken,

    /// <summary>The plan was already full, over, or the deadline had passed: the invitation is Unsuccessful.</summary>
    TooLate,

    /// <summary>The invitation was answered or ended already; nothing changed.</summary>
    NotPending,
}

/// <summary>The rules of a work-day plan as pure functions on the entities, so they are unit tested and every endpoint gives one answer.</summary>
public static class WorkDayPlanRules
{
    public const int MinRequiredPeople = 1;
    public const int MaxRequiredPeople = 500;
    public const int MaxReasonLength = 500;
    public const int MaxNoteLength = 500;
    public const int MaxPeoplePerRequest = 200;
    public const int MaxPayeeLength = 200;
    public const int DefaultDeadlineMinutes = 30;

    /// <summary>The minutes an invitation may wait for its answer, chosen per send.</summary>
    public static readonly IReadOnlyList<int> DeadlineChoices = [15, 30, 60];

    public static bool ValidRequiredPeople(int? value) => value is >= MinRequiredPeople and <= MaxRequiredPeople;

    /// <summary>The job type of the kind, by its name in the catalogue (it sets the shift rate).</summary>
    public static string JobNameOf(WorkDayPlanKind kind) => kind switch
    {
        WorkDayPlanKind.Harvest => RecordCrewJobs.Harvest,
        WorkDayPlanKind.Nursery => RecordCrewJobs.Nursery,
        WorkDayPlanKind.Seeding => RecordCrewJobs.Seeding,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>The kind of the work a job type of the catalogue names; null for any other job type.</summary>
    public static WorkDayPlanKind? KindOfJob(string? jobName) =>
        string.Equals(jobName, RecordCrewJobs.Harvest, StringComparison.OrdinalIgnoreCase) ? WorkDayPlanKind.Harvest
        : string.Equals(jobName, RecordCrewJobs.Nursery, StringComparison.OrdinalIgnoreCase) ? WorkDayPlanKind.Nursery
        : string.Equals(jobName, RecordCrewJobs.Seeding, StringComparison.OrdinalIgnoreCase) ? WorkDayPlanKind.Seeding
        : null;

    /// <summary>The people on the plan who work: the confirmed members and the people hired outside. The person in charge does not count.</summary>
    public static int Present(WorkDayPlan plan) => plan.Members.Count(m => m.IsConfirmed) + plan.OutsiderHires.Sum(h => h.People);

    public static int Missing(WorkDayPlan plan) => Math.Max(0, plan.RequiredPeople - Present(plan));

    /// <summary>Staffing can change until the plan is over.</summary>
    public static bool Open(WorkDayPlanStatus status) => status is WorkDayPlanStatus.Staffing or WorkDayPlanStatus.Ready or WorkDayPlanStatus.InProgress;

    /// <summary>The plan has all the people it needs, whatever its status.</summary>
    public static bool IsFull(WorkDayPlan plan) => Present(plan) >= plan.RequiredPeople;

    /// <summary>The plan is Ready or running: it has the people and may start (or has started) its work.</summary>
    public static bool IsStaffed(WorkDayPlanStatus status) => status is WorkDayPlanStatus.Ready or WorkDayPlanStatus.InProgress or WorkDayPlanStatus.Completed;

    /// <summary>
    /// Brings the status in line with the people: Staffing becomes Ready when the present people reach the number needed, and Ready goes back to Staffing when they no longer do (a removal or a raised
    /// number). A plan that is running does not go back. Once the plan is full the invitations still waiting end as Unsuccessful. Nothing else changes.
    /// </summary>
    public static WorkDayPlanSettlement Settle(WorkDayPlan plan, DateTimeOffset now)
    {
        var from = plan.Status;
        if (!Open(from))
        {
            return new WorkDayPlanSettlement(from, from, []);
        }

        var full = IsFull(plan);
        var to = from switch
        {
            WorkDayPlanStatus.Staffing when full => WorkDayPlanStatus.Ready,
            WorkDayPlanStatus.Ready when !full => WorkDayPlanStatus.Staffing,
            _ => from,
        };
        plan.Status = to;

        var ended = new List<WorkDayPlanMember>();
        if (full)
        {
            foreach (var member in plan.Members.Where(m => m.State == WorkDayMemberState.Invited))
            {
                member.State = WorkDayMemberState.Unsuccessful;
                member.RespondedAt = now;
                ended.Add(member);
            }
        }

        return new WorkDayPlanSettlement(from, to, ended);
    }

    /// <summary>
    /// An employee answers an invitation. A pending invitation of a plan that is full, over or past the deadline becomes Unsuccessful whatever the answer; otherwise the answer is taken
    /// (Accepted or Declined, with the optional reason of a decline). An answer to an invitation that is no longer pending changes nothing.
    /// </summary>
    public static WorkDayAnswer Answer(WorkDayPlan plan, WorkDayPlanMember member, bool accept, string? reason, DateTimeOffset now)
    {
        if (member.State != WorkDayMemberState.Invited)
        {
            return WorkDayAnswer.NotPending;
        }

        member.RespondedAt = now;
        if (!Open(plan.Status) || IsFull(plan) || member.Deadline is { } deadline && now > deadline)
        {
            member.State = WorkDayMemberState.Unsuccessful;
            return WorkDayAnswer.TooLate;
        }

        member.State = accept ? WorkDayMemberState.Accepted : WorkDayMemberState.Declined;
        member.Reason = accept ? null : reason;
        return WorkDayAnswer.Taken;
    }

    /// <summary>Invitations past their deadline end as Expired; returns the members changed.</summary>
    public static IReadOnlyList<WorkDayPlanMember> ExpireOverdue(WorkDayPlan plan, DateTimeOffset now)
    {
        var overdue = plan.Members.Where(m => m.State == WorkDayMemberState.Invited && m.Deadline is { } d && d <= now).ToList();
        foreach (var member in overdue)
        {
            member.State = WorkDayMemberState.Expired;
            member.RespondedAt = now;
        }

        return overdue;
    }

    /// <summary>A person may be invited again after an earlier invitation ended without them working.</summary>
    public static bool CanInviteAgain(WorkDayMemberState state) =>
        state is WorkDayMemberState.Declined or WorkDayMemberState.Expired or WorkDayMemberState.Unsuccessful;

    /// <summary>The Owner or the Zone Manager confirms for a person whose invitation is pending or has ended without them working.</summary>
    public static bool CanConfirmOnBehalf(WorkDayMemberState state) => state != WorkDayMemberState.Accepted && state != WorkDayMemberState.ConfirmedOnBehalf;

    /// <summary>The plan still lacks people: it is Staffing.</summary>
    public static bool IsShort(WorkDayPlan plan) => plan.Status == WorkDayPlanStatus.Staffing;

    /// <summary>
    /// The plan of a harvest line follows the line: Completed when the line is done (its lot recorded or every lane left out), open again (Staffing or Ready by its people) when the line is open again.
    /// Returns whether the plan changed.
    /// </summary>
    public static bool FollowHarvestLine(WorkDayPlan plan, bool lineDone, DateTimeOffset now)
    {
        if (lineDone && Open(plan.Status))
        {
            plan.Status = WorkDayPlanStatus.Completed;
            plan.CompletedAt = now;
            return true;
        }

        if (!lineDone && plan.Status == WorkDayPlanStatus.Completed)
        {
            plan.Status = WorkDayPlanStatus.Staffing;
            plan.CompletedAt = null;
            Settle(plan, now);
            return true;
        }

        return false;
    }

    /// <summary>The Vietnamese name of a status, for messages.</summary>
    public static string StatusName(WorkDayPlanStatus status) => status switch
    {
        WorkDayPlanStatus.Staffing => "Chưa đủ người",
        WorkDayPlanStatus.Ready => "Đủ người",
        WorkDayPlanStatus.InProgress => "Đang làm",
        WorkDayPlanStatus.Completed => "Hoàn thành",
        _ => "Đã hủy",
    };
}
