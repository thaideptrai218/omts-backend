using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Farming;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Labor;

namespace OMTS.Application.Features.Labor.Work;

/// <summary>The employee codes a harvest plan line's crew gained and lost.</summary>
public sealed record LineCrewChange(IReadOnlyList<string> Added, IReadOnlyList<string> Removed);

/// <summary>
/// LABOR-02: a Zone Manager assigns employees a job type and a raft for a date, edits or removes an assignment until the employee is
/// checked in, and changes the job of a whole crew during the day (BR-LABOR-03). The Cooperative Owner does the same in every zone.
/// The client asks MSG-LABOR-06 and MSG-GEN-04 before it sends. Notifying the employee (MSG-LABOR-05) needs the employee app and its
/// linked device (LABOR-06), which does not exist yet, so nothing is pushed.
/// </summary>
public sealed class WorkAssignmentService(
    IWorkAssignmentRepository assignments,
    IAttendanceRepository attendances,
    IWorkEmployeeReader employeeReader,
    IEmployeeRepository employees,
    IEmployeeRequestRepository requests,
    IRaftRepository rafts,
    IFarmingZoneRepository farmingZones,
    ZoneAccessGuard zones,
    PayPeriodLock payPeriods,
    IStatusCatalog statusCatalog,
    ICurrentUser currentUser,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    private const int MaxEmployees = 100;

    /// <summary>LABOR-02.0: one assignment per employee, all or none. An employee already on another raft is moved only with <c>moveExisting</c> (LABOR-02.4).</summary>
    public async Task<Result<IReadOnlyList<AssignmentView>>> AssignAsync(AssignWorkRequest request, CancellationToken cancellationToken)
    {
        var today = timeProvider.Today();
        var check = new FieldCheck();
        check.Check(HasPlace(request.RaftId, request.ZoneId), "Raft");
        check.Check(request.JobTypeId is { } j && j != Guid.Empty, "Job type");
        check.Check(request.Date is { } d && d >= today, "Date (today or later)");
        var ids = request.EmployeeIds ?? [];
        check.Check(ids.Count is >= 1 and <= MaxEmployees && ids.Distinct().Count() == ids.Count && ids.All(i => i != Guid.Empty), "Employees");
        if (!check.IsValid)
        {
            return AppError.InvalidFields(check.Invalid);
        }

        var date = request.Date!.Value;
        var chosen = await ChooseAsync(request.RaftId, request.ZoneId, request.JobTypeId!.Value, PermissionAction.Create, cancellationToken);
        if (!chosen.IsSuccess)
        {
            return chosen.Error!;
        }

        var (place, jobType) = chosen.Value;
        var people = await employeeReader.GetForWorkAsync(ids, cancellationToken);
        if (people.Count != ids.Count)
        {
            return AppError.Invalid("Employees");
        }

        if (people.FirstOrDefault(e => !WorkRules.IsEmployedOn(e, date)) is { } left)
        {
            return AppError.NotUsableChoice($"Employee {left.FullName}", statusCatalog.DisplayNameOf(StatusIds.Employee.Left), "Employees");
        }

        if (await PlaceAsync(people, date, place, jobType, request.MoveExisting, null, null, null, cancellationToken) is { } refused)
        {
            return refused;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await RowsAsync(ids, date, cancellationToken);
    }

    /// <summary>
    /// The crew of one harvest plan line (the Nhân công of a plan), all or none: the employees named get the first assignment of the day on the line's raft with the job type, as
    /// <see cref="AssignAsync"/> gives it, and are linked to the line; the employees linked to the line and not named are taken off it, as <see cref="RemoveAsync"/> does, which refuses one who is checked in.
    /// Nothing is saved: the caller adds its own audit record and saves once. The plan's own rules (who may staff in which status) are the caller's.
    /// </summary>
    /// <param name="line">The line, with its raft and zone.</param>
    /// <param name="date">The business date of the plan.</param>
    /// <param name="jobTypeId">The job type of the work (Thu hoạch).</param>
    /// <param name="employeeIds">The whole crew wanted, at most 100, each once; empty takes everyone off.</param>
    /// <param name="moveExisting">Moves an employee who is already on another raft that day (LABOR-02.4); without it that is a conflict.</param>
    public async Task<Result<LineCrewChange>> StageLineCrewAsync(
        HarvestPlanLine line, DateOnly date, Guid jobTypeId, IReadOnlyList<Guid> employeeIds, bool moveExisting, CancellationToken cancellationToken)
    {
        if (employeeIds.Count > MaxEmployees || employeeIds.Distinct().Count() != employeeIds.Count || employeeIds.Any(i => i == Guid.Empty))
        {
            return AppError.InvalidFields(["Employees"]);
        }

        var linked = await assignments.ListOfPlanLinesAsync([line.Id], cancellationToken);
        var wanted = employeeIds.ToHashSet();
        var removing = linked.Where(a => !wanted.Contains(a.EmployeeId)).ToList();
        var have = linked.Select(a => a.EmployeeId).ToHashSet();
        var adding = employeeIds.Where(e => !have.Contains(e)).ToList();
        if (removing.Count == 0 && adding.Count == 0)
        {
            return new LineCrewChange([], []);
        }

        if (date < timeProvider.Today())
        {
            return AppError.Invalid("Date (today or later)");
        }

        foreach (var assignment in removing)
        {
            if (!await zones.AllowsAsync(PermissionFunctions.WorkAssignments, PermissionAction.Delete, assignment.ZoneId, cancellationToken))
            {
                return AppError.Forbidden;
            }

            if (assignment.WorkDayPlanId is not null)
            {
                return OnPlan(assignment);
            }

            if (await RefuseChangeAsync(assignment, cancellationToken) is { } refused)
            {
                return refused;
            }
        }

        IReadOnlyList<Employee> added = [];
        if (adding.Count > 0)
        {
            var chosen = await ChooseAsync(line.RaftId, null, jobTypeId, PermissionAction.Create, cancellationToken);
            if (!chosen.IsSuccess)
            {
                return chosen.Error!;
            }

            var (place, jobType) = chosen.Value;
            var people = await employeeReader.GetForWorkAsync(adding, cancellationToken);
            if (people.Count != adding.Count)
            {
                return AppError.Invalid("Employees");
            }

            if (people.FirstOrDefault(e => !WorkRules.IsEmployedOn(e, date)) is { } left)
            {
                return AppError.NotUsableChoice($"Employee {left.FullName}", statusCatalog.DisplayNameOf(StatusIds.Employee.Left), "Employees");
            }

            if (await PlaceAsync(people, date, place, jobType, moveExisting, line.Id, null, null, cancellationToken) is { } placed)
            {
                return placed;
            }

            added = people;
        }

        foreach (var assignment in removing)
        {
            assignments.Remove(assignment);
            audit.Record(new AuditEntry("WorkAssignment.Removed", nameof(WorkAssignment))
            {
                RecordId = assignment.Id, RecordCode = assignment.Employee.Code, ZoneId = assignment.ZoneId,
                Before = Snapshot(assignment.Employee, assignment.Date, PlaceOf(assignment), assignment.JobType),
            });
        }

        return new LineCrewChange(added.Select(e => e.Code ?? e.FullName).Order().ToList(), removing.Select(a => a.Employee.Code ?? a.Employee.FullName).Order().ToList());
    }

    /// <summary>
    /// The people who are confirmed on a work-day plan get the first assignment of the plan's day on its place (the raft, or the zone for a nursery plan) with the job type of its kind, linked to the plan, by the rules of
    /// <see cref="AssignAsync"/>: an open pay period, nobody checked in, and a person already on another raft (or on another plan) that day moved only with <paramref name="moveExisting"/> (MSG-LABOR-06). Nothing is saved:
    /// the caller adds its own audit record and saves once. Returns the assignment each person has for the plan.
    /// </summary>
    public async Task<Result<IReadOnlyDictionary<Guid, WorkAssignment>>> StagePlanCrewAsync(
        WorkDayPlan plan, Guid jobTypeId, IReadOnlyList<Guid> employeeIds, bool moveExisting, CancellationToken cancellationToken)
    {
        if (employeeIds.Count is < 1 or > MaxEmployees || employeeIds.Distinct().Count() != employeeIds.Count || employeeIds.Any(i => i == Guid.Empty))
        {
            return AppError.InvalidFields(["Employees"]);
        }

        if (plan.WorkDate < timeProvider.Today())
        {
            return AppError.Invalid("Date (today or later)");
        }

        var chosen = await ChooseAsync(plan.RaftId, plan.ZoneId, jobTypeId, PermissionAction.Create, cancellationToken);
        if (!chosen.IsSuccess)
        {
            return chosen.Error!;
        }

        var (place, jobType) = chosen.Value;
        var people = await employeeReader.GetForWorkAsync(employeeIds, cancellationToken);
        if (people.Count != employeeIds.Count)
        {
            return AppError.Invalid("Employees");
        }

        if (people.FirstOrDefault(e => !WorkRules.IsEmployedOn(e, plan.WorkDate)) is { } left)
        {
            return AppError.NotUsableChoice($"Employee {left.FullName}", statusCatalog.DisplayNameOf(StatusIds.Employee.Left), "Employees");
        }

        var placed = new List<WorkAssignment>();
        if (await PlaceAsync(people, plan.WorkDate, place, jobType, moveExisting, null, plan.Id, placed, cancellationToken) is { } refused)
        {
            return refused;
        }

        return Result<IReadOnlyDictionary<Guid, WorkAssignment>>.Success(placed.GroupBy(a => a.EmployeeId).ToDictionary(g => g.Key, g => g.Last()));
    }

    /// <summary>
    /// Takes the assignment of a member who is taken off a work-day plan, as <see cref="RemoveAsync"/> does (the zone right, the day not past, nobody checked in, the pay period open: MSG-LABOR-07
    /// for a person who checked in). Nothing is saved.
    /// </summary>
    public async Task<AppError?> StageRemovePlanAssignmentAsync(WorkAssignment assignment, CancellationToken cancellationToken)
    {
        if (!await zones.AllowsAsync(PermissionFunctions.WorkAssignments, PermissionAction.Delete, assignment.ZoneId, cancellationToken))
        {
            return AppError.Forbidden;
        }

        if (await RefuseChangeAsync(assignment, cancellationToken) is { } refused)
        {
            return refused;
        }

        assignments.Remove(assignment);
        audit.Record(new AuditEntry("WorkAssignment.Removed", nameof(WorkAssignment))
        {
            RecordId = assignment.Id, RecordCode = assignment.Employee.Code, ZoneId = assignment.ZoneId,
            Before = Snapshot(assignment.Employee, assignment.Date, PlaceOf(assignment), assignment.JobType),
        });
        return null;
    }

    /// <summary>An assignment made for a work-day plan is changed through the plan (409, <c>reasonCode</c> ASSIGNMENT_ON_PLAN).</summary>
    private static AppError OnPlan(WorkAssignment assignment) => AppError.Conflict(
        UserMessages.WorkPlanRefused($"Phân công của {assignment.Employee.FullName} thuộc một kế hoạch ngày làm. Hãy đổi hoặc bỏ người này trong kế hoạch ngày làm."),
        new Dictionary<string, object?> { ["reasonCode"] = "ASSIGNMENT_ON_PLAN", ["workDayPlanId"] = assignment.WorkDayPlanId });

    private static bool OnAnotherPlan(WorkAssignment first, Guid? workDayPlanId) =>
        workDayPlanId is { } planId && first.WorkDayPlanId is { } other && other != planId;

    /// <summary>
    /// The rules every first assignment of a day follows (LABOR-02.0), checked for all the employees before anything changes: the pay period is open, no one is checked in, and an employee on another raft is moved only
    /// with <paramref name="moveExisting"/>. Then each gets the assignment or has it changed, and the zone joins the working zones. Nothing is saved: the caller saves. <paramref name="planLineId"/> links the
    /// assignments to a harvest plan line; a plain assignment that moves one to another raft clears the link.
    /// </summary>
    private async Task<AppError?> PlaceAsync(
        IReadOnlyList<Employee> people, DateOnly date, Place place, JobType jobType, bool moveExisting, Guid? planLineId, Guid? workDayPlanId, List<WorkAssignment>? placed,
        CancellationToken cancellationToken)
    {
        if (await payPeriods.CheckAsync(date, cancellationToken) is { } locked)
        {
            return locked;
        }

        var ids = people.Select(e => e.Id).ToList();
        var existing = (await assignments.ListOfEmployeesAsync(ids, date, cancellationToken)).ToLookup(w => w.EmployeeId);
        var attended = (await attendances.ListOfEmployeesAsync(ids, date, cancellationToken)).ToDictionary(a => a.EmployeeId);
        // Busy only while working: an employee who has checked out is free again and starts a new session (the day is reopened below).
        bool Finished(Employee e) => attended.TryGetValue(e.Id, out var a) && a.CheckInAt is not null && a.CheckOutAt is not null;
        if (people.FirstOrDefault(e => attended.TryGetValue(e.Id, out var a) && a.CheckInAt is not null && a.CheckOutAt is null) is { } checkedIn)
        {
            return AlreadyCheckedIn(checkedIn);
        }

        var elsewhere = people
            .Where(e => !Finished(e))
            .Select(e => (Employee: e, First: existing[e.Id].FirstOrDefault(w => w.StartsAt is null)))
            .Where(x => x.First is not null && (!place.Holds(x.First) || OnAnotherPlan(x.First, workDayPlanId)))
            .ToList();
        // The people of a work-day plan are moved through the plan, so its members and its count stay right.
        if (workDayPlanId is null && elsewhere.FirstOrDefault(x => x.First!.WorkDayPlanId is not null) is { First: { } planned })
        {
            return OnPlan(planned);
        }

        if (elsewhere.Count > 0 && !moveExisting)
        {
            var (employee, first) = elsewhere[0];
            return AppError.Conflict(
                WorkMessages.MoveToRaft(employee.FullName, PlaceOf(first!).Code, date, place.Code),
                new Dictionary<string, object?> { ["employeeIds"] = elsewhere.Select(x => x.Employee.Id).ToList() });
        }

        // Taking a worker off a raft of another zone is changing that zone's work: the permission of the zone the worker leaves is needed as well (stricter than LABOR-02.4, PM decision of 17/11/2026).
        foreach (var leaving in elsewhere.Select(x => x.First!.ZoneId).Where(z => z != place.ZoneId).Distinct())
        {
            if (!await zones.AllowsAsync(PermissionFunctions.WorkAssignments, PermissionAction.Edit, leaving, cancellationToken))
            {
                return AppError.Forbidden;
            }
        }

        var account = currentUser.AccountId ?? throw new InvalidOperationException("Work is assigned by a logged-in user.");
        foreach (var employee in people)
        {
            if (Finished(employee))
            {
                placed?.Add(ReopenDay(employee, attended[employee.Id], existing[employee.Id].ToList(), date, place, jobType, planLineId, workDayPlanId, account));
                AddWorkingZone(employee, place.ZoneId);
                continue;
            }

            var first = existing[employee.Id].FirstOrDefault(w => w.StartsAt is null);
            if (first is null)
            {
                var created = new WorkAssignment
                {
                    EmployeeId = employee.Id, Date = date, RaftId = place.Raft?.Id, ZoneId = place.ZoneId, JobTypeId = jobType.Id, AssignedByAccountId = account, HarvestPlanLineId = planLineId,
                    WorkDayPlanId = workDayPlanId,
                };
                assignments.Add(created);
                placed?.Add(created);
                audit.Record(new AuditEntry("WorkAssignment.Created", nameof(WorkAssignment))
                {
                    RecordId = created.Id, RecordCode = employee.Code, ZoneId = place.ZoneId, After = Snapshot(employee, date, place, jobType),
                });
            }
            else
            {
                var moved = !place.Holds(first) || OnAnotherPlan(first, workDayPlanId);
                var link = planLineId ?? (moved ? null : first.HarvestPlanLineId);
                var planLink = workDayPlanId ?? (moved ? null : first.WorkDayPlanId);
                placed?.Add(first);
                if (moved || first.JobTypeId != jobType.Id || link != first.HarvestPlanLineId || planLink != first.WorkDayPlanId)
                {
                    var before = Snapshot(employee, date, PlaceOf(first), first.JobType);
                    (first.RaftId, first.ZoneId) = (place.Raft?.Id, place.ZoneId);
                    first.JobTypeId = jobType.Id;
                    first.HarvestPlanLineId = link;
                    first.WorkDayPlanId = planLink;
                    first.AssignedByAccountId = account;
                    audit.Record(new AuditEntry(moved ? "WorkAssignment.Moved" : "WorkAssignment.Updated", nameof(WorkAssignment))
                    {
                        RecordId = first.Id, RecordCode = employee.Code, ZoneId = place.ZoneId, Before = before, After = Snapshot(employee, date, place, jobType),
                    });
                }
            }

            AddWorkingZone(employee, place.ZoneId);
        }

        return null;
    }

    /// <summary>
    /// A new session for an employee who checked out: the check-out is cleared (the audit row keeps the old place and the old check-out) and, when the place or the job differs from the latest period,
    /// a new job period starts now there, so the earlier session keeps its place. One open session per employee stays true: the day has one attendance record, open again.
    /// </summary>
    private WorkAssignment ReopenDay(Employee employee, Attendance record, List<WorkAssignment> periods, DateOnly date, Place place, JobType jobType, Guid? planLineId, Guid? workDayPlanId, Guid account)
    {
        WorkAssignment period;
        var last = periods.OrderBy(p => p.StartsAt is null ? 0 : 1).ThenBy(p => p.StartsAt).LastOrDefault();
        var before = new { record.CheckInAt, record.CheckOutAt, Place = last is null ? null : PlaceOf(last).Code, Job = last?.JobType.Name };
        if (last is null)
        {
            period = new WorkAssignment { EmployeeId = employee.Id, Date = date, RaftId = place.Raft?.Id, ZoneId = place.ZoneId, JobTypeId = jobType.Id, AssignedByAccountId = account, HarvestPlanLineId = planLineId, WorkDayPlanId = workDayPlanId };
            assignments.Add(period);
        }
        else
        {
            // The earlier session ends at the old check-out and a new period starts now, even on the same raft and job, so the gap between the two sessions is not counted as work (L1, BR-LABOR-05).
            last.EndsAt ??= record.CheckOutAt;
            var sameWork = place.Holds(last) && last.JobTypeId == jobType.Id;
            period = new WorkAssignment
            {
                EmployeeId = employee.Id, Date = date, RaftId = place.Raft?.Id, ZoneId = place.ZoneId, JobTypeId = jobType.Id, StartsAt = WorkRules.Now(timeProvider), AssignedByAccountId = account,
                HarvestPlanLineId = planLineId ?? (sameWork ? last.HarvestPlanLineId : null),
                WorkDayPlanId = workDayPlanId ?? (sameWork ? last.WorkDayPlanId : null),
            };
            assignments.Add(period);
        }

        (record.CheckOutAt, record.CheckOutMethod, record.CheckOutRecordedByAccountId, record.CheckOutReason) = (null, null, null, null);
        unitOfWork.MarkChanged(record);
        audit.Record(new AuditEntry("Attendance.Reopened", nameof(Attendance))
        {
            RecordId = record.Id, RecordCode = employee.Code, ZoneId = place.ZoneId, Before = before, After = new { record.CheckInAt, CheckOutAt = (DateTimeOffset?)null, Place = place.Code, Job = jobType.Name },
            Reason = "Employee assigned again after checking out.",
        });
        return period;
    }

    /// <summary>LABOR-02.2: the raft or the job type of an assignment of today or later, before the employee is checked in.</summary>
    public async Task<Result<AssignmentView>> EditAsync(Guid id, EditAssignmentRequest request, CancellationToken cancellationToken)
    {
        var check = new FieldCheck();
        check.Check(HasPlace(request.RaftId, request.ZoneId), "Raft");
        check.Check(request.JobTypeId is { } j && j != Guid.Empty, "Job type");
        if (!check.IsValid)
        {
            return AppError.InvalidFields(check.Invalid);
        }

        var assignment = await assignments.GetAsync(id, cancellationToken);
        if (assignment is null)
        {
            return AppError.NotFound;
        }

        if (!await zones.AllowsAsync(PermissionFunctions.WorkAssignments, PermissionAction.Edit, assignment.ZoneId, cancellationToken))
        {
            return AppError.Forbidden;
        }

        if (assignment.WorkDayPlanId is not null)
        {
            return OnPlan(assignment);
        }

        if (await RefuseChangeAsync(assignment, cancellationToken) is { } refused)
        {
            return refused;
        }

        var unchanged = assignment.JobTypeId == request.JobTypeId
            && (request.RaftId is null ? assignment.RaftId is null && assignment.ZoneId == request.ZoneId : assignment.RaftId == request.RaftId);
        if (!unchanged)
        {
            var chosen = await ChooseAsync(request.RaftId, request.ZoneId, request.JobTypeId!.Value, PermissionAction.Edit, cancellationToken);
            if (!chosen.IsSuccess)
            {
                return chosen.Error!;
            }

            var (place, jobType) = chosen.Value;
            var before = Snapshot(assignment.Employee, assignment.Date, PlaceOf(assignment), assignment.JobType);
            if (!place.Holds(assignment))
            {
                assignment.HarvestPlanLineId = null;
                assignment.WorkDayPlanId = null;
            }

            (assignment.RaftId, assignment.ZoneId) = (place.Raft?.Id, place.ZoneId);
            assignment.JobTypeId = jobType.Id;
            assignment.AssignedByAccountId = currentUser.AccountId ?? throw new InvalidOperationException("Work is assigned by a logged-in user.");
            audit.Record(new AuditEntry("WorkAssignment.Updated", nameof(WorkAssignment))
            {
                RecordId = assignment.Id, RecordCode = assignment.Employee.Code, ZoneId = place.ZoneId, Before = before, After = Snapshot(assignment.Employee, assignment.Date, place, jobType),
            });
            var employee = (await employeeReader.GetForWorkAsync([assignment.EmployeeId], cancellationToken))[0];
            AddWorkingZone(employee, place.ZoneId);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return (await RowsAsync([assignment.EmployeeId], assignment.Date, cancellationToken)).Value![0];
    }

    /// <summary>LABOR-02.2: removes an assignment of today or later whose employee is not checked in. Ask MSG-GEN-04 first.</summary>
    public async Task<Result<Done>> RemoveAsync(Guid id, CancellationToken cancellationToken)
    {
        var assignment = await assignments.GetAsync(id, cancellationToken);
        if (assignment is null)
        {
            return AppError.NotFound;
        }

        if (!await zones.AllowsAsync(PermissionFunctions.WorkAssignments, PermissionAction.Delete, assignment.ZoneId, cancellationToken))
        {
            return AppError.Forbidden;
        }

        if (assignment.WorkDayPlanId is not null)
        {
            return OnPlan(assignment);
        }

        if (await RefuseChangeAsync(assignment, cancellationToken) is { } refused)
        {
            return refused;
        }

        assignments.Remove(assignment);
        audit.Record(new AuditEntry("WorkAssignment.Removed", nameof(WorkAssignment))
        {
            RecordId = assignment.Id, RecordCode = assignment.Employee.Code, ZoneId = assignment.ZoneId,
            Before = Snapshot(assignment.Employee, assignment.Date, PlaceOf(assignment), assignment.JobType),
        });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Done.Value;
    }

    /// <summary>
    /// LABOR-02.1: today, each selected member who is checked in and not checked out gets a new job period from now, and each who is not
    /// checked in yet has the assignment changed (BR-LABOR-03). It sends no notification (BR-LABOR-04). Returns how many members changed.
    /// </summary>
    public async Task<Result<int>> ChangeCrewJobAsync(CrewJobRequest request, CancellationToken cancellationToken)
    {
        var today = timeProvider.Today();
        var check = new FieldCheck();
        check.Check(HasPlace(request.RaftId, request.ZoneId), "Raft");
        check.Check(request.JobTypeId is { } j && j != Guid.Empty, "Job type");
        var ids = request.EmployeeIds ?? [];
        check.Check(ids.Count is >= 1 and <= MaxEmployees && ids.Distinct().Count() == ids.Count && ids.All(i => i != Guid.Empty), "Employees");
        if (!check.IsValid)
        {
            return AppError.InvalidFields(check.Invalid);
        }

        var chosen = await ChooseAsync(request.RaftId, request.ZoneId, request.JobTypeId!.Value, PermissionAction.Edit, cancellationToken);
        if (!chosen.IsSuccess)
        {
            return chosen.Error!;
        }

        var (place, jobType) = chosen.Value;
        if (await payPeriods.CheckAsync(today, cancellationToken) is { } locked)
        {
            return locked;
        }

        var periods = (await assignments.ListOfEmployeesAsync(ids, today, cancellationToken)).ToLookup(w => w.EmployeeId);
        var attended = (await attendances.ListOfEmployeesAsync(ids, today, cancellationToken)).ToDictionary(a => a.EmployeeId);
        var now = WorkRules.Now(timeProvider);
        var account = currentUser.AccountId ?? throw new InvalidOperationException("Work is changed by a logged-in user.");
        var changed = 0;
        foreach (var id in ids)
        {
            var ordered = periods[id].OrderBy(p => p.StartsAt is null ? 0 : 1).ThenBy(p => p.StartsAt).ToList();
            attended.TryGetValue(id, out var record);
            // A member of the crew has work today and is not checked out (LABOR-02.1 step 2).
            if (ordered.Count == 0 || record?.CheckOutAt is not null)
            {
                return AppError.Invalid("Employees");
            }

            var current = ordered[^1];
            if (!await zones.AllowsAsync(PermissionFunctions.WorkAssignments, PermissionAction.Edit, current.ZoneId, cancellationToken))
            {
                return AppError.Forbidden;
            }

            if (place.Holds(current) && current.JobTypeId == jobType.Id)
            {
                continue;
            }

            var before = Snapshot(current.Employee, today, PlaceOf(current), current.JobType);
            if (record?.CheckInAt is null)
            {
                if (!place.Holds(current))
                {
                    current.HarvestPlanLineId = null;
                    current.WorkDayPlanId = null;
                }

                (current.RaftId, current.ZoneId) = (place.Raft?.Id, place.ZoneId);
                current.JobTypeId = jobType.Id;
                current.AssignedByAccountId = account;
            }
            else
            {
                assignments.Add(new WorkAssignment { EmployeeId = id, Date = today, RaftId = place.Raft?.Id, ZoneId = place.ZoneId, JobTypeId = jobType.Id, StartsAt = now, AssignedByAccountId = account });
                // The Owner's correction of the day carries the version of the attendance record: a new job period gives the record a new version, so a correction screen read before it is refused (MSG06)
                // instead of removing the period without a word (b3 of the review of 13/11/2026).
                if (record is not null)
                {
                    unitOfWork.MarkChanged(record);
                }
            }

            audit.Record(new AuditEntry("WorkAssignment.JobChanged", nameof(WorkAssignment))
            {
                RecordId = current.Id, RecordCode = current.Employee.Code, ZoneId = place.ZoneId, Before = before, After = Snapshot(current.Employee, today, place, jobType),
                Reason = record?.CheckInAt is null ? null : $"New job period from {BusinessClock.Format(now)}.",
            });
            changed++;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return changed;
    }

    /// <summary>Where the work is: a raft, or a whole zone when it has no raft (nursery). <see cref="Code"/> names it in messages.</summary>
    private sealed record Place(Guid ZoneId, Raft? Raft, string Code)
    {
        /// <summary>Whether the assignment is already there.</summary>
        public bool Holds(WorkAssignment a) => Raft is null ? a.RaftId is null && a.ZoneId == ZoneId : a.RaftId == Raft.Id;
    }

    private static bool HasPlace(Guid? raftId, Guid? zoneId) =>
        raftId is { } r ? r != Guid.Empty : zoneId is { } z && z != Guid.Empty;

    private static Place PlaceOf(WorkAssignment a) =>
        new(a.ZoneId, a.Raft, a.Raft?.Code ?? $"{a.Zone.Code} (zone)");

    /// <summary>The raft In Use (or, with no raft, the zone In Use) the caller may act in, and the job type In Use (GBR-01, BR-FARM-08).</summary>
    private async Task<Result<(Place Place, JobType JobType)>> ChooseAsync(Guid? raftId, Guid? zoneId, Guid jobTypeId, PermissionAction action, CancellationToken cancellationToken)
    {
        Place place;
        if (raftId is { } rid)
        {
            var raft = (await rafts.GetByIdsAsync([rid], cancellationToken)).FirstOrDefault();
            if (raft is null)
            {
                return AppError.Invalid("Raft");
            }

            if (!await zones.AllowsAsync(PermissionFunctions.WorkAssignments, action, raft.ZoneId, cancellationToken))
            {
                return AppError.Forbidden;
            }

            if (raft.StatusId != StatusIds.Usage.InUse)
            {
                return AppError.NotUsableChoice($"Raft {raft.Code}", statusCatalog.DisplayNameOf(raft.StatusId), "Raft");
            }

            place = new Place(raft.ZoneId, raft, raft.Code);
        }
        else
        {
            var zone = (await farmingZones.GetByIdsAsync([zoneId!.Value], cancellationToken)).FirstOrDefault();
            if (zone is null)
            {
                return AppError.Invalid("Raft");
            }

            if (!await zones.AllowsAsync(PermissionFunctions.WorkAssignments, action, zone.Id, cancellationToken))
            {
                return AppError.Forbidden;
            }

            if (zone.StatusId != StatusIds.Usage.InUse)
            {
                return AppError.NotUsableChoice($"Zone {zone.Code}", statusCatalog.DisplayNameOf(zone.StatusId), "Zone");
            }

            place = new Place(zone.Id, null, $"{zone.Code} (zone)");
        }

        var jobType = (await assignments.GetJobTypesAsync([jobTypeId], cancellationToken)).FirstOrDefault();
        if (jobType is null)
        {
            return AppError.Invalid("Job type");
        }

        return jobType.StatusId == StatusIds.Usage.InUse
            ? (place, jobType)
            : AppError.NotUsableChoice($"Job type {jobType.Name}", statusCatalog.DisplayNameOf(jobType.StatusId), "Job type");
    }

    /// <summary>An assignment can be edited or removed from today on, until its employee is checked in (BR-LABOR-03), and only its first period.</summary>
    private async Task<AppError?> RefuseChangeAsync(WorkAssignment assignment, CancellationToken cancellationToken)
    {
        if (assignment.Date < timeProvider.Today() || assignment.StartsAt is not null)
        {
            return AppError.Invalid("Date (today or later)");
        }

        if (await attendances.GetAsync(assignment.EmployeeId, assignment.Date, cancellationToken) is { CheckInAt: not null })
        {
            return AlreadyCheckedIn(assignment.Employee);
        }

        return await payPeriods.CheckAsync(assignment.Date, cancellationToken);
    }

    /// <summary>
    /// The zone of the work joins the working zones of an employee who does not have it (LABOR-02 step 9, BR-LABOR-01), unless the employee is a
    /// seasonal worker: a seasonal worker has no fixed zone (today zone A, tomorrow zone B by assignment, decision of 25/11/2026).
    /// </summary>
    private void AddWorkingZone(Employee employee, Guid zoneId)
    {
        if (employee.WorkingZones.Any(w => w.ZoneId == zoneId) || WorkRules.TypeOn(employee, timeProvider.Today()) == OMTS.Domain.Enums.EmployeeType.SeasonalWorker)
        {
            return;
        }

        employees.AddWorkingZone(new EmployeeWorkingZone { EmployeeId = employee.Id, ZoneId = zoneId });
        unitOfWork.MarkChanged(employee);
        audit.Record(new AuditEntry("Employee.WorkingZoneAdded", nameof(Employee)) { RecordId = employee.Id, ZoneId = zoneId, After = new { ZoneId = zoneId } });
    }

    private async Task<Result<IReadOnlyList<AssignmentView>>> RowsAsync(IReadOnlyCollection<Guid> employeeIds, DateOnly date, CancellationToken cancellationToken)
    {
        var periods = await assignments.ListOfEmployeesAsync(employeeIds, date, cancellationToken);
        var attended = (await attendances.ListOfEmployeesAsync(employeeIds, date, cancellationToken)).ToDictionary(a => a.EmployeeId);
        var leave = (await requests.ListApprovedLeaveAsync(employeeIds, date, cancellationToken)).Select(l => l.EmployeeId).ToHashSet();
        return Result<IReadOnlyList<AssignmentView>>.Success(WorkRules.Rows(periods, attended, leave));
    }

    private static AppError AlreadyCheckedIn(Employee employee) => AppError.Conflict(
        WorkMessages.AlreadyCheckedIn(employee.FullName), new Dictionary<string, object?> { ["employeeId"] = employee.Id });

    private static object Snapshot(Employee employee, DateOnly date, Place place, JobType jobType) => new
    {
        EmployeeId = employee.Id, Employee = employee.FullName, Date = date, RaftId = place.Raft?.Id, Raft = place.Raft?.Code, place.ZoneId, JobTypeId = jobType.Id, JobType = jobType.Name,
    };
}
