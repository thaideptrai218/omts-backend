using OMTS.Application.Common;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Employees;
using OMTS.Application.Features.Labor;
using OMTS.Application.Features.Labor.Work;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.WorkDayPlans;

/// <summary>
/// The people of a work-day plan. The Owner, or the Zone Manager of the plan's zone, invites employees (each with a deadline to answer), confirms for a person with a reason, takes a person off and sets the number of
/// people needed. A confirmed person has the first assignment of the plan's day on its place with the job type of its kind, by the rules of <see cref="WorkAssignmentService"/> (an open pay period, MSG-LABOR-06 for a person on
/// another raft or plan with a move prompt, MSG-LABOR-07 for a person who checked in). Every change settles the status of the plan (<see cref="WorkDayPlanSettler"/>): full when the confirmed people plus the people hired
/// outside reach the number needed; a plan short of people cannot run.
/// </summary>
public sealed class WorkDayPlanStaffingService(
    IWorkDayPlanRepository plans,
    IWorkAssignmentRepository assignments,
    IAttendanceRepository attendances,
    IEmployeeRepository employees,
    IEmployeeRequestRepository leaves,
    IJobTypeRepository jobTypes,
    EmployeeCallerAccess callers,
    WorkAssignmentService work,
    WorkDayPlanAccess access,
    WorkDayPlanViews views,
    WorkDayPlanSettler settler,
    WorkDayPlanNotifier notifier,
    PayPeriodLock payPeriods,
    RowVersionGuard rowVersions,
    ICurrentUser user,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    private const string RecordType = nameof(WorkDayPlan);

    // ---- Choosing the people ----

    /// <summary>
    /// Invites employees: each is put on the plan as Invited with the deadline (15, 30 or 60 minutes from now) and told. A person invited and not answered, or who answered Yes or No, is skipped; a person whose earlier invitation ended
    /// (declined, expired, unsuccessful) is invited again. 409 when the plan is full (nobody more is needed), over, or its day has passed; 400 MSG-GEN-01 <c>Employees</c> for an employee who is not working or not one the caller sees.
    /// </summary>
    public async Task<Result<WorkDayPlanItem>> InviteAsync(Guid planId, InviteWorkDayPlanRequest request, CancellationToken cancellationToken)
    {
        var ids = request.EmployeeIds?.Distinct().ToList();
        var minutes = request.DeadlineMinutes ?? WorkDayPlanRules.DefaultDeadlineMinutes;
        var invalid = new List<string>();
        if (ids is null || ids.Count is 0 or > WorkDayPlanRules.MaxPeoplePerRequest || ids.Any(i => i == Guid.Empty))
        {
            invalid.Add("Employees");
        }

        if (!WorkDayPlanRules.DeadlineChoices.Contains(minutes))
        {
            invalid.Add("Deadline minutes");
        }

        if (invalid.Count > 0)
        {
            return AppError.InvalidFields(invalid);
        }

        var loaded = await LoadForStaffAsync(planId, cancellationToken);
        if (!loaded.IsSuccess)
        {
            return loaded.Error!;
        }

        var plan = loaded.Value!;
        if (plan.WorkDate < timeProvider.Today())
        {
            return PastDay(plan);
        }

        if (WorkDayPlanRules.IsFull(plan))
        {
            return Refused($"Kế hoạch {plan.Code} đã đủ người nên không cần mời thêm.", "PLAN_FULL");
        }

        var caller = await callers.GetAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var deadline = now.AddMinutes(minutes);
        var invited = new List<WorkDayPlanMember>();
        foreach (var employeeId in ids!)
        {
            var member = plan.Members.FirstOrDefault(m => m.EmployeeId == employeeId);
            if (member is not null)
            {
                if (!WorkDayPlanRules.CanInviteAgain(member.State))
                {
                    continue;
                }

                (member.State, member.InvitedAt, member.Deadline, member.RespondedAt, member.Reason, member.AddedByAccountId) =
                    (WorkDayMemberState.Invited, now, deadline, null, null, user.AccountId);
                unitOfWork.MarkChanged(member);
            }
            else
            {
                var employee = await employees.GetForEditAsync(employeeId, cancellationToken);
                if (employee is null || employee.StatusId != StatusIds.Employee.Working || !caller.Sees(employee))
                {
                    return AppError.InvalidFields(["Employees"]);
                }

                member = new WorkDayPlanMember
                {
                    Plan = plan, PlanId = plan.Id, Employee = employee, EmployeeId = employee.Id, State = WorkDayMemberState.Invited, InvitedAt = now, Deadline = deadline, AddedByAccountId = user.AccountId,
                };
                plans.AddMember(member);
                if (!plan.Members.Contains(member))
                {
                    plan.Members.Add(member);
                }
            }

            invited.Add(member);
            audit.Record(new AuditEntry("WorkDayPlan.MemberInvited", RecordType)
            {
                RecordId = plan.Id, RecordCode = plan.Code, ZoneId = plan.ZoneId, After = new { Employee = member.Employee.FullName, member.Employee.Code, Deadline = deadline },
            });
        }

        await notifier.InvitedAsync(plan, invited.Select(m => m.Employee.AccountId).OfType<Guid>().ToList(), deadline, cancellationToken);
        await settler.SettleAsync(plan, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await views.DetailAsync(plan, cancellationToken);
    }

    /// <summary>
    /// Confirms for a person who has not answered, declined or whose invitation ended: they get the assignment of the day at once. The reason is required (at most 500 characters). 404 for a member not on the plan; 409 MSG-LABOR-06
    /// (the person is on another raft or plan that day: send <c>moveExisting</c> to move them) or MSG-LABOR-07 (checked in) or the pay period lock; a person already confirmed stays as is.
    /// </summary>
    public async Task<Result<WorkDayPlanItem>> ConfirmAsync(Guid planId, Guid memberId, ConfirmWorkDayMemberRequest request, CancellationToken cancellationToken)
    {
        var reason = request.Reason?.Trim();
        if (string.IsNullOrEmpty(reason) || reason.Length > WorkDayPlanRules.MaxReasonLength)
        {
            return AppError.InvalidFields(["Reason"]);
        }

        var loaded = await LoadForStaffAsync(planId, cancellationToken);
        if (!loaded.IsSuccess)
        {
            return loaded.Error!;
        }

        var plan = loaded.Value!;
        var member = plan.Members.FirstOrDefault(m => m.Id == memberId);
        if (member is null)
        {
            return AppError.NotFound;
        }

        if (!WorkDayPlanRules.CanConfirmOnBehalf(member.State))
        {
            return await views.DetailAsync(plan, cancellationToken);
        }

        if (plan.WorkDate < timeProvider.Today())
        {
            return PastDay(plan);
        }

        var job = await JobOfAsync(plan, cancellationToken);
        if (!job.IsSuccess)
        {
            return job.Error!;
        }

        var staged = await work.StagePlanCrewAsync(plan, job.Value, [member.EmployeeId], request.MoveExisting, cancellationToken);
        if (!staged.IsSuccess)
        {
            return staged.Error!;
        }

        // A person moved from another plan of the day leaves it: that plan loses them (and may be short again).
        await LeaveOtherPlansAsync(plan, member.EmployeeId, cancellationToken);

        var before = member.State;
        member.State = WorkDayMemberState.ConfirmedOnBehalf;
        member.Reason = reason;
        member.RespondedAt = timeProvider.GetUtcNow();
        member.ConfirmedByAccountId = user.AccountId;
        member.WorkAssignmentId = staged.Value![member.EmployeeId].Id;
        unitOfWork.MarkChanged(member);
        audit.Record(new AuditEntry("WorkDayPlan.MemberConfirmedOnBehalf", RecordType)
        {
            RecordId = plan.Id,
            RecordCode = plan.Code,
            ZoneId = plan.ZoneId,
            Before = new { Employee = member.Employee.FullName, State = before.ToString() },
            After = new { Employee = member.Employee.FullName, State = member.State.ToString() },
            Reason = reason,
        });
        await settler.SettleAsync(plan, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await views.DetailAsync(plan, cancellationToken);
    }

    /// <summary>
    /// Takes a person off the plan, in any state; a confirmed person loses the assignment of the day, unless they checked in (409 MSG-LABOR-07). A plan that is no longer full goes back to Staffing, unless the work started.
    /// </summary>
    public async Task<Result<WorkDayPlanItem>> RemoveMemberAsync(Guid planId, Guid memberId, RemoveWorkDayMemberRequest? request, CancellationToken cancellationToken)
    {
        var reason = request?.Reason?.Trim();
        if (reason is { Length: > WorkDayPlanRules.MaxReasonLength })
        {
            return AppError.InvalidFields(["Reason"]);
        }

        var loaded = await LoadForStaffAsync(planId, cancellationToken);
        if (!loaded.IsSuccess)
        {
            return loaded.Error!;
        }

        var plan = loaded.Value!;
        var member = plan.Members.FirstOrDefault(m => m.Id == memberId);
        if (member is null)
        {
            return AppError.NotFound;
        }

        if (member.WorkAssignmentId is { } assignmentId && await assignments.GetAsync(assignmentId, cancellationToken) is { } assignment && assignment.WorkDayPlanId == plan.Id)
        {
            if (await work.StageRemovePlanAssignmentAsync(assignment, cancellationToken) is { } refused)
            {
                return refused;
            }
        }

        plan.Members.Remove(member);
        plans.RemoveMember(member);
        audit.Record(new AuditEntry("WorkDayPlan.MemberRemoved", RecordType)
        {
            RecordId = plan.Id,
            RecordCode = plan.Code,
            ZoneId = plan.ZoneId,
            Before = new { Employee = member.Employee.FullName, member.Employee.Code, State = member.State.ToString() },
            Reason = string.IsNullOrEmpty(reason) ? null : reason,
        });
        await settler.SettleAsync(plan, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await views.DetailAsync(plan, cancellationToken);
    }

    /// <summary>
    /// Sets the number of people needed (1 to 500). Raising it can make a Ready plan short again (before the work starts); lowering it can make a Staffing plan Ready, which ends the invitations still waiting.
    /// 400 MSG-GEN-01 <c>Required people</c>; 409 MSG06 for a stale <c>rowVersion</c>; 403 for a Zone Manager on a harvest plan (the Owner sets it).
    /// </summary>
    public async Task<Result<WorkDayPlanItem>> SetRequiredPeopleAsync(Guid planId, SetWorkDayRequiredPeopleRequest request, CancellationToken cancellationToken)
    {
        if (!WorkDayPlanRules.ValidRequiredPeople(request.RequiredPeople))
        {
            return AppError.InvalidFields(["Required people"]);
        }

        var loaded = await LoadForStaffAsync(planId, cancellationToken);
        if (!loaded.IsSuccess)
        {
            return loaded.Error!;
        }

        var plan = loaded.Value!;
        if (!WorkDayPlanAccess.MayEdit((await access.CurrentAsync(cancellationToken))!, plan))
        {
            return AppError.Forbidden;
        }

        if (request.RowVersion is not null && await rowVersions.CheckAsync(plan, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        if (plan.RequiredPeople != request.RequiredPeople)
        {
            audit.Record(new AuditEntry("WorkDayPlan.RequiredPeopleChanged", RecordType)
            {
                RecordId = plan.Id, RecordCode = plan.Code, ZoneId = plan.ZoneId, Before = new { plan.RequiredPeople }, After = new { RequiredPeople = request.RequiredPeople },
            });
            plan.RequiredPeople = request.RequiredPeople!.Value;
        }

        await settler.SettleAsync(plan, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await views.DetailAsync(plan, cancellationToken);
    }

    // ---- Warnings for the picker ----

    /// <summary>
    /// What the day looks like for these employees, for the picker to warn (never to block): approved leave, an assignment on a raft or plan, or already checked in. The caller needs WORK_ASSIGNMENTS Create in some zone (the API checks).
    /// </summary>
    public async Task<Result<IReadOnlyList<WorkDayAvailabilityRow>>> AvailabilityAsync(DateOnly date, IReadOnlyCollection<Guid>? employeeIds, CancellationToken cancellationToken)
    {
        var ids = employeeIds?.Distinct().ToList();
        if (date == default || ids is null || ids.Count is 0 or > WorkDayPlanRules.MaxPeoplePerRequest)
        {
            return AppError.InvalidFields([date == default ? "Date" : "Employees"]);
        }

        var onLeave = (await leaves.ListApprovedLeaveAsync(ids, date, cancellationToken)).Select(l => l.EmployeeId).ToHashSet();
        var periods = (await assignments.ListOfEmployeesAsync(ids, date, cancellationToken)).Where(a => a.StartsAt is null).ToDictionary(a => a.EmployeeId);
        var attended = (await attendances.ListOfEmployeesAsync(ids, date, cancellationToken)).Where(a => a.CheckInAt is not null).Select(a => a.EmployeeId).ToHashSet();
        return Result<IReadOnlyList<WorkDayAvailabilityRow>>.Success(ids.Select(id =>
        {
            periods.TryGetValue(id, out var first);
            return new WorkDayAvailabilityRow(id, onLeave.Contains(id), attended.Contains(id), first?.WorkDayPlanId, first is null ? null : first.Raft?.Code ?? $"{first.Zone.Code} (zone)");
        }).ToList());
    }

    // ---- Following the plan ----

    /// <summary>
    /// The assignments of the plan follow its new day and place: they move to <paramref name="newDate"/>, <paramref name="newZoneId"/> and <paramref name="newRaftId"/>. Everything is checked before anything changes: nobody on the
    /// plan has checked in, the new day is today or later, the pay periods of both days are open, and nobody has other work or a check-in on the new day. A refusal is the answer and leaves the assignments as they are.
    /// Nothing is saved: the caller saves with its own change.
    /// </summary>
    public async Task<AppError?> FollowPlaceAsync(WorkDayPlan plan, DateOnly newDate, Guid newZoneId, Guid? newRaftId, CancellationToken cancellationToken)
    {
        var linked = (await assignments.ListOfPlansAsync([plan.Id], cancellationToken)).Where(a => a.WorkDayPlanId == plan.Id).ToList();
        var moving = linked.Where(a => a.Date != newDate || a.ZoneId != newZoneId || a.RaftId != newRaftId).ToList();
        if (moving.Count == 0)
        {
            return null;
        }

        foreach (var day in moving.Select(a => a.Date).Distinct())
        {
            var ids = moving.Where(a => a.Date == day).Select(a => a.EmployeeId).ToList();
            if ((await attendances.ListOfEmployeesAsync(ids, day, cancellationToken)).FirstOrDefault(a => a.CheckInAt is not null) is { } attended)
            {
                return AppError.Conflict(
                    WorkMessages.AlreadyCheckedIn(moving.First(a => a.EmployeeId == attended.EmployeeId).Employee.FullName),
                    new Dictionary<string, object?> { ["employeeId"] = attended.EmployeeId });
            }

            if (await payPeriods.CheckAsync(day, cancellationToken) is { } lockedOld)
            {
                return lockedOld;
            }
        }

        if (moving.Any(a => a.Date != newDate))
        {
            if (newDate < timeProvider.Today())
            {
                return Refused($"Không thể chuyển kế hoạch {plan.Code} sang ngày {newDate:dd/MM/yyyy} đã qua.", "PAST_DAY");
            }

            if (await payPeriods.CheckAsync(newDate, cancellationToken) is { } lockedNew)
            {
                return lockedNew;
            }

            var ids = moving.Select(a => a.EmployeeId).ToList();
            var taken = (await assignments.ListOfEmployeesAsync(ids, newDate, cancellationToken)).FirstOrDefault(a => a.StartsAt is null);
            var attendedNew = (await attendances.ListOfEmployeesAsync(ids, newDate, cancellationToken)).FirstOrDefault(a => a.CheckInAt is not null);
            if (taken is not null || attendedNew is not null)
            {
                var name = moving.First(a => a.EmployeeId == (taken?.EmployeeId ?? attendedNew!.EmployeeId)).Employee.FullName;
                return AppError.Conflict(UserMessages.PlanStaffingDateTaken(plan.Code, name, newDate));
            }
        }

        foreach (var assignment in moving)
        {
            var from = Snapshot(assignment, plan);
            (assignment.Date, assignment.ZoneId, assignment.RaftId) = (newDate, newZoneId, newRaftId);
            audit.Record(new AuditEntry("WorkAssignment.Updated", nameof(WorkAssignment))
            {
                RecordId = assignment.Id, RecordCode = assignment.Employee.Code, ZoneId = newZoneId, Before = from, After = Snapshot(assignment, plan),
                Reason = $"Kế hoạch ngày làm {plan.Code} đổi ngày hoặc nơi làm.",
            });
        }

        return null;
    }

    /// <summary>
    /// The plan is cancelled: the assignments of today or later with no check-in are removed, the others stay as the record of the work (without the link). Nothing is saved.
    /// </summary>
    public async Task ReleaseAssignmentsAsync(WorkDayPlan plan, CancellationToken cancellationToken)
    {
        var linked = (await assignments.ListOfPlansAsync([plan.Id], cancellationToken)).Where(a => a.WorkDayPlanId == plan.Id).ToList();
        var today = timeProvider.Today();
        foreach (var assignment in linked)
        {
            var checkedIn = (await attendances.GetAsync(assignment.EmployeeId, assignment.Date, cancellationToken)) is { CheckInAt: not null };
            if (checkedIn || assignment.Date < today)
            {
                assignment.WorkDayPlanId = null;
                continue;
            }

            assignments.Remove(assignment);
            audit.Record(new AuditEntry("WorkAssignment.Removed", nameof(WorkAssignment))
            {
                RecordId = assignment.Id, RecordCode = assignment.Employee.Code, ZoneId = assignment.ZoneId, Before = Snapshot(assignment, plan), Reason = $"Kế hoạch ngày làm {plan.Code} đã hủy.",
            });
        }

        foreach (var member in plan.Members.Where(m => m.WorkAssignmentId is not null))
        {
            member.WorkAssignmentId = null;
        }
    }

    // ---- Building ----

    /// <summary>The plan with its people when the caller sees it (404), may staff it (403) and it is not over (409).</summary>
    private async Task<Result<WorkDayPlan>> LoadForStaffAsync(Guid planId, CancellationToken cancellationToken)
    {
        var who = await access.CurrentAsync(cancellationToken);
        if (who is null)
        {
            return AppError.Forbidden;
        }

        var plan = await plans.GetForEditAsync(planId, cancellationToken);
        if (plan is null || !WorkDayPlanAccess.MaySee(who, plan))
        {
            return AppError.NotFound;
        }

        if (!WorkDayPlanAccess.MayStaff(who, plan))
        {
            return AppError.Forbidden;
        }

        return WorkDayPlanRules.Open(plan.Status)
            ? plan
            : Refused($"Kế hoạch {plan.Code} đang ở trạng thái {WorkDayPlanRules.StatusName(plan.Status)} nên không thể chọn hay đổi nhân công.", "PLAN_CLOSED");
    }

    /// <summary>The job type of the plan's kind, In Use.</summary>
    private async Task<Result<Guid>> JobOfAsync(WorkDayPlan plan, CancellationToken cancellationToken)
    {
        var name = WorkDayPlanRules.JobNameOf(plan.Kind);
        var job = await jobTypes.FindByNameAsync(name, null, cancellationToken);
        return job is { StatusId: StatusIds.Usage.InUse } ? job.Id : AppError.Conflict(UserMessages.PlanStaffingJobMissing(name));
    }

    /// <summary>A person moved onto this plan leaves the other plans of the day: the confirmed row goes, and a plan that is no longer full is Staffing again.</summary>
    private async Task LeaveOtherPlansAsync(WorkDayPlan plan, Guid employeeId, CancellationToken cancellationToken)
    {
        foreach (var other in await plans.ListMembersOnDayAsync([employeeId], plan.WorkDate, plan.Id, cancellationToken))
        {
            other.Plan.Members.Remove(other);
            plans.RemoveMember(other);
            audit.Record(new AuditEntry("WorkDayPlan.MemberRemoved", RecordType)
            {
                RecordId = other.PlanId,
                RecordCode = other.Plan.Code,
                ZoneId = other.Plan.ZoneId,
                Before = new { Employee = other.Employee.FullName, State = other.State.ToString() },
                Reason = $"Chuyển sang kế hoạch {plan.Code}.",
            });
            await settler.SettleAsync(other.Plan, cancellationToken);
        }
    }

    private static AppError PastDay(WorkDayPlan plan) =>
        Refused($"Ngày {plan.WorkDate:dd/MM/yyyy} của kế hoạch {plan.Code} đã qua nên không thể chọn hay đổi nhân công.", "PAST_DAY");

    internal static AppError Refused(string text, string reasonCode) =>
        AppError.Conflict(UserMessages.WorkPlanRefused(text), new Dictionary<string, object?> { ["reasonCode"] = reasonCode });

    private static object Snapshot(WorkAssignment assignment, WorkDayPlan plan) => new
    {
        assignment.EmployeeId, Employee = assignment.Employee.FullName, assignment.Date, assignment.RaftId, Raft = assignment.Raft?.Code, assignment.ZoneId, Plan = plan.Code,
    };
}
