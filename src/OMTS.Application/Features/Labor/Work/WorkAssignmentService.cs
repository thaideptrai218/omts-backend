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
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Labor;

namespace OMTS.Application.Features.Labor.Work;

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

        if (await payPeriods.CheckAsync(date, cancellationToken) is { } locked)
        {
            return locked;
        }

        var existing = (await assignments.ListOfEmployeesAsync(ids, date, cancellationToken)).ToLookup(w => w.EmployeeId);
        var attended = (await attendances.ListOfEmployeesAsync(ids, date, cancellationToken)).ToDictionary(a => a.EmployeeId);
        if (people.FirstOrDefault(e => attended.TryGetValue(e.Id, out var a) && a.CheckInAt is not null) is { } checkedIn)
        {
            return AlreadyCheckedIn(checkedIn);
        }

        var elsewhere = people
            .Select(e => (Employee: e, First: existing[e.Id].FirstOrDefault(w => w.StartsAt is null)))
            .Where(x => x.First is not null && !place.Holds(x.First))
            .ToList();
        if (elsewhere.Count > 0 && !request.MoveExisting)
        {
            var (employee, first) = elsewhere[0];
            return AppError.Conflict(
                WorkMessages.MoveToRaft(employee.FullName, PlaceOf(first!).Code, date, place.Code),
                new Dictionary<string, object?> { ["employeeIds"] = elsewhere.Select(x => x.Employee.Id).ToList() });
        }

        var account = currentUser.AccountId ?? throw new InvalidOperationException("Work is assigned by a logged-in user.");
        foreach (var employee in people)
        {
            var first = existing[employee.Id].FirstOrDefault(w => w.StartsAt is null);
            if (first is null)
            {
                var created = new WorkAssignment { EmployeeId = employee.Id, Date = date, RaftId = place.Raft?.Id, ZoneId = place.ZoneId, JobTypeId = jobType.Id, AssignedByAccountId = account };
                assignments.Add(created);
                audit.Record(new AuditEntry("WorkAssignment.Created", nameof(WorkAssignment))
                {
                    RecordId = created.Id, RecordCode = employee.Code, ZoneId = place.ZoneId, After = Snapshot(employee, date, place, jobType),
                });
            }
            else if (!place.Holds(first) || first.JobTypeId != jobType.Id)
            {
                var before = Snapshot(employee, date, PlaceOf(first), first.JobType);
                var moved = !place.Holds(first);
                (first.RaftId, first.ZoneId) = (place.Raft?.Id, place.ZoneId);
                first.JobTypeId = jobType.Id;
                first.AssignedByAccountId = account;
                audit.Record(new AuditEntry(moved ? "WorkAssignment.Moved" : "WorkAssignment.Updated", nameof(WorkAssignment))
                {
                    RecordId = first.Id, RecordCode = employee.Code, ZoneId = place.ZoneId, Before = before, After = Snapshot(employee, date, place, jobType),
                });
            }

            AddWorkingZone(employee, place.ZoneId);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await RowsAsync(ids, date, cancellationToken);
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
                (current.RaftId, current.ZoneId) = (place.Raft?.Id, place.ZoneId);
                current.JobTypeId = jobType.Id;
                current.AssignedByAccountId = account;
            }
            else
            {
                assignments.Add(new WorkAssignment { EmployeeId = id, Date = today, RaftId = place.Raft?.Id, ZoneId = place.ZoneId, JobTypeId = jobType.Id, StartsAt = now, AssignedByAccountId = account });
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

    /// <summary>The zone of the work joins the working zones of an employee who does not have it (LABOR-02 step 9, BR-LABOR-01).</summary>
    private void AddWorkingZone(Employee employee, Guid zoneId)
    {
        if (employee.WorkingZones.Any(w => w.ZoneId == zoneId))
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
