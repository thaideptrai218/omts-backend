using OMTS.Application.Common;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Labor;
using OMTS.Application.Features.Labor.Work;
using OMTS.Application.Features.Workforce;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Entities.Labor;

namespace OMTS.Application.Features.Harvest.Plans;

/// <summary>
/// The "Nhân công" of a harvest plan (PM decision of 17/11/2026): for each line the planner picks the employees, and saving gives each a work assignment (job type Thu hoạch) on the raft of the line for the day of the plan,
/// linked to the line. Taking an employee off removes the assignment, unless the employee checked in that day. The Owner chooses the crew while the plan is a Draft or Assigned, the Zone Manager of the zone once it is
/// Assigned or Harvesting, nobody after. The assignment rules are those of <see cref="WorkAssignmentService"/>. A plan that changes day, drops a line or changes the raft of one, or is cancelled, carries its assignments with it
/// (<see cref="FollowAsync"/>). The harvest record already suggests the crew of a raft from the assignments of the day (<c>HarvestCrewService</c>), so a crew chosen here reaches it.
/// </summary>
public sealed class HarvestPlanStaffingService(
    IHarvestPlanRepository plans,
    IWorkAssignmentRepository assignments,
    IAttendanceRepository attendances,
    IJobTypeRepository jobTypes,
    WorkAssignmentService work,
    HarvestPlanAccess access,
    ZoneAccessGuard zones,
    PayPeriodLock payPeriods,
    IStatusCatalog statusCatalog,
    RowVersionGuard rowVersions,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    // ---- Reading ----

    /// <summary>The crew of every line of the plan the caller sees: every line for the Owner, the lines of its zones for a Zone Manager.</summary>
    public async Task<Result<HarvestPlanStaffing>> GetAsync(Guid planId, CancellationToken cancellationToken)
    {
        var plan = await plans.GetForEditAsync(planId, cancellationToken);
        if (plan is null || !await access.MayReadAsync(plan, cancellationToken))
        {
            return AppError.NotFound;
        }

        var lines = await LinesAsync(plan, (await access.VisibleLinesAsync(plan, cancellationToken)).OrderBy(l => l.LineNumber).ToList(), cancellationToken);
        return new HarvestPlanStaffing(plan.Id, plan.Code, RowVersionGuard.Encode(plan.RowVersion), lines, lines.Any(l => l.CanEdit));
    }

    // ---- Choosing the crew ----

    /// <summary>
    /// Sets the whole crew of a line. 404 for a line the caller does not see; 403 for a caller who may not staff it; 409 MSG-GEN-09 once the plan is Completed or Cancelled, when the day has passed or when there is no job
    /// type Thu hoạch in use; the work assignment rules answer the rest (an employee checked in cannot be taken off: 409 MSG-LABOR-07; one on another raft: 409 MSG-LABOR-06 unless <c>moveExisting</c>).
    /// </summary>
    public async Task<Result<HarvestPlanStaffingLine>> SaveLineAsync(Guid planId, Guid lineId, SaveHarvestPlanLineStaffingRequest request, CancellationToken cancellationToken)
    {
        var plan = await plans.GetForEditAsync(planId, cancellationToken);
        var line = plan?.Lines.FirstOrDefault(l => l.Id == lineId);
        if (plan is null || line is null || !(await access.VisibleLinesAsync(plan, cancellationToken)).Any(l => l.Id == lineId))
        {
            return AppError.NotFound;
        }

        var refusal = await RefusalAsync(plan, line, cancellationToken);
        if (refusal == StaffingRefusal.Forbidden)
        {
            return AppError.Forbidden;
        }

        if (refusal == StaffingRefusal.WrongStatus)
        {
            return AppError.Conflict(UserMessages.PlanStaffingClosed(plan.Code, statusCatalog.DisplayNameOf(plan.StatusId)));
        }

        if (request.EmployeeIds is null)
        {
            return AppError.InvalidFields(["Employees"]);
        }

        if (request.RowVersion is not null && await rowVersions.CheckAsync(plan, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        var current = await assignments.ListOfPlanLinesAsync([line.Id], cancellationToken);
        var before = current.Select(a => a.Employee.Code ?? a.Employee.FullName).Order().ToList();
        var job = await jobTypes.FindByNameAsync(RecordCrewJobs.Harvest, null, cancellationToken);
        if (request.EmployeeIds.Count > 0 && (job is null || job.StatusId != StatusIds.Usage.InUse))
        {
            return AppError.Conflict(UserMessages.PlanStaffingJobMissing(RecordCrewJobs.Harvest));
        }

        var day = plan.PlannedDate;
        if (!HarvestPlanStaffingRules.DayOpen(day, timeProvider.Today()))
        {
            if (!current.Select(a => a.EmployeeId).Order().SequenceEqual(request.EmployeeIds.Distinct().Order()))
            {
                return AppError.Conflict(UserMessages.PlanStaffingPastDate(plan.Code, day));
            }
        }

        var staged = await work.StageLineCrewAsync(line, day, job?.Id ?? Guid.Empty, request.EmployeeIds, request.MoveExisting, cancellationToken);
        if (!staged.IsSuccess)
        {
            return staged.Error!;
        }

        var change = staged.Value!;
        if (change.Added.Count > 0 || change.Removed.Count > 0)
        {
            audit.Record(new AuditEntry("HarvestPlan.StaffingChanged", HarvestPlanService.RecordType)
            {
                RecordId = plan.Id,
                RecordCode = plan.Code,
                ZoneId = line.Raft.ZoneId,
                Before = new { Raft = line.Raft.Code, Date = day, Employees = before },
                After = new { Raft = line.Raft.Code, Date = day, Employees = before.Except(change.Removed).Concat(change.Added).Order().ToList(), change.Added, change.Removed },
            });
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return (await LinesAsync(plan, [line], cancellationToken))[0];
    }

    // ---- Following the plan ----

    /// <summary>
    /// A plan that is changed or cancelled carries its assignments with it; nothing is saved here, the caller saves with the change of the plan. <paramref name="leavingLineIds"/> are the lines that are taken out of the
    /// plan, change their raft, or all of them when the plan is cancelled: their assignments are removed when of today or later with no check-in, and unlinked otherwise (the work stays on record). The assignments of
    /// the other lines move to <paramref name="newDate"/> unless the employee checked in on their day. Everything is checked before anything changes; a refusal (the new day has passed, is in a closed pay period or the
    /// employee has other work or is checked in that day) is the answer and leaves the assignments as they are.
    /// </summary>
    public async Task<AppError?> FollowAsync(HarvestPlan plan, DateOnly newDate, IReadOnlySet<Guid> leavingLineIds, CancellationToken cancellationToken)
    {
        // The assignments made for a work-day plan follow it through the plan of the line; the rest follow here.
        var linked = (await assignments.ListOfPlanLinesAsync(plan.Lines.Select(l => l.Id).ToList(), cancellationToken)).Where(a => a.WorkDayPlanId is null).ToList();
        if (linked.Count == 0)
        {
            return null;
        }

        var today = timeProvider.Today();
        var checkedIn = new HashSet<(Guid, DateOnly)>();
        foreach (var day in linked.Select(a => a.Date).Distinct())
        {
            var ids = linked.Where(a => a.Date == day).Select(a => a.EmployeeId).ToList();
            foreach (var record in await attendances.ListOfEmployeesAsync(ids, day, cancellationToken))
            {
                if (record.CheckInAt is not null)
                {
                    checkedIn.Add((record.EmployeeId, day));
                }
            }
        }

        var decided = linked.Select(a => (Assignment: a, Follow: leavingLineIds.Contains(a.HarvestPlanLineId!.Value)
            ? HarvestPlanStaffingRules.WhenLineGoes(a.Date, today, checkedIn.Contains((a.EmployeeId, a.Date)))
            : HarvestPlanStaffingRules.WhenDateChanges(a.Date, newDate, checkedIn.Contains((a.EmployeeId, a.Date))))).ToList();
        var moving = decided.Where(x => x.Follow == StaffingFollow.Move).Select(x => x.Assignment).ToList();
        if (moving.Count > 0 && await RefuseMoveAsync(plan, moving, newDate, today, cancellationToken) is { } refused)
        {
            return refused;
        }

        foreach (var (assignment, follow) in decided)
        {
            switch (follow)
            {
                case StaffingFollow.Remove:
                    assignments.Remove(assignment);
                    audit.Record(new AuditEntry("WorkAssignment.Removed", nameof(WorkAssignment))
                    {
                        RecordId = assignment.Id,
                        RecordCode = assignment.Employee.Code,
                        ZoneId = assignment.ZoneId,
                        Before = Snapshot(assignment, plan),
                        Reason = $"Kế hoạch thu hoạch {plan.Code} đổi hoặc hủy.",
                    });
                    break;
                case StaffingFollow.Unlink:
                    assignment.HarvestPlanLineId = null;
                    break;
                case StaffingFollow.Move:
                    var from = Snapshot(assignment, plan);
                    assignment.Date = newDate;
                    audit.Record(new AuditEntry("WorkAssignment.Updated", nameof(WorkAssignment))
                    {
                        RecordId = assignment.Id,
                        RecordCode = assignment.Employee.Code,
                        ZoneId = assignment.ZoneId,
                        Before = from,
                        After = Snapshot(assignment, plan),
                        Reason = $"Kế hoạch thu hoạch {plan.Code} đổi ngày.",
                    });
                    break;
            }
        }

        return null;
    }

    /// <summary>The new day is today or later, in an open pay period, and no employee who moves with it is already checked in or assigned that day.</summary>
    private async Task<AppError?> RefuseMoveAsync(HarvestPlan plan, IReadOnlyList<WorkAssignment> moving, DateOnly newDate, DateOnly today, CancellationToken cancellationToken)
    {
        if (!HarvestPlanStaffingRules.DayOpen(newDate, today))
        {
            return AppError.Conflict(UserMessages.PlanStaffingNewDatePast(plan.Code, newDate));
        }

        if (await payPeriods.CheckAsync(newDate, cancellationToken) is { } locked)
        {
            return locked;
        }

        var ids = moving.Select(a => a.EmployeeId).ToList();
        var taken = (await assignments.ListOfEmployeesAsync(ids, newDate, cancellationToken)).FirstOrDefault(a => a.StartsAt is null);
        if (taken is not null)
        {
            return AppError.Conflict(UserMessages.PlanStaffingDateTaken(plan.Code, taken.Employee.FullName, newDate));
        }

        var attended = (await attendances.ListOfEmployeesAsync(ids, newDate, cancellationToken)).FirstOrDefault(a => a.CheckInAt is not null);
        return attended is null
            ? null
            : AppError.Conflict(UserMessages.PlanStaffingDateTaken(plan.Code, moving.First(a => a.EmployeeId == attended.EmployeeId).Employee.FullName, newDate));
    }

    // ---- Building ----

    private async Task<StaffingRefusal> RefusalAsync(HarvestPlan plan, HarvestPlanLine line, CancellationToken cancellationToken) =>
        HarvestPlanStaffingRules.Refusal(
            plan.StatusId,
            await access.IsOwnerAsync(cancellationToken),
            await zones.AllowsAsync(PermissionFunctions.WorkAssignments, PermissionAction.Create, line.Raft.ZoneId, cancellationToken));

    private async Task<IReadOnlyList<HarvestPlanStaffingLine>> LinesAsync(HarvestPlan plan, IReadOnlyList<HarvestPlanLine> lines, CancellationToken cancellationToken)
    {
        var linked = (await assignments.ListOfPlanLinesAsync(lines.Select(l => l.Id).ToList(), cancellationToken)).ToLookup(a => a.HarvestPlanLineId!.Value);
        var checkedIn = new HashSet<(Guid, DateOnly)>();
        foreach (var day in lines.SelectMany(l => linked[l.Id]).Select(a => a.Date).Distinct())
        {
            var ids = lines.SelectMany(l => linked[l.Id]).Where(a => a.Date == day).Select(a => a.EmployeeId).Distinct().ToList();
            foreach (var record in await attendances.ListOfEmployeesAsync(ids, day, cancellationToken))
            {
                if (record.CheckInAt is not null)
                {
                    checkedIn.Add((record.EmployeeId, day));
                }
            }
        }

        var open = HarvestPlanStaffingRules.DayOpen(plan.PlannedDate, timeProvider.Today());
        var result = new List<HarvestPlanStaffingLine>();
        foreach (var line in lines)
        {
            var canEdit = open && await RefusalAsync(plan, line, cancellationToken) == StaffingRefusal.None;
            result.Add(new HarvestPlanStaffingLine(
                line.Id, line.RaftId, line.Raft.Code, line.Raft.ZoneId, line.Raft.Zone.Code, line.Lanes.Select(l => l.Lane.LaneNumber).Order().ToList(), plan.PlannedDate,
                linked[line.Id].Select(a => new HarvestPlanStaffingEmployee(a.EmployeeId, a.Employee.Code ?? string.Empty, a.Employee.FullName, a.Id, checkedIn.Contains((a.EmployeeId, a.Date))))
                    .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(e => e.EmployeeId).ToList(),
                canEdit));
        }

        return result;
    }

    private static object Snapshot(WorkAssignment assignment, HarvestPlan plan) => new
    {
        assignment.EmployeeId, Employee = assignment.Employee.FullName, assignment.Date, assignment.RaftId, Raft = assignment.Raft?.Code, assignment.ZoneId, Plan = plan.Code,
    };
}
