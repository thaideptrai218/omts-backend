using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Labor;

namespace OMTS.Application.Features.Labor.WorkAssignments;

/// <summary>
/// The checks every change of work assignments shares (BR-LABOR-03): the raft is In Use and in the zones the caller works
/// in, the job type is In Use, the date is today or later and no locked pay period holds it, and the employee is not checked in.
/// </summary>
public sealed class WorkAssignmentGuard(
    IWorkAssignmentRepository assignments,
    IAttendanceDayReader attendance,
    ZoneAccessGuard zoneAccess,
    PayPeriodLock payPeriods,
    IStatusCatalog statusCatalog,
    TimeProvider timeProvider)
{
    /// <summary>The most employees one request names: a crew, not the cooperative.</summary>
    public const int MaxEmployees = 200;

    public DateOnly Today() => timeProvider.Today();

    /// <summary>
    /// The raft with its zone, and the caller's right on that zone. A raft of the route is 404 when unknown and MSG-GEN-09 when
    /// Discontinued; a raft chosen in the body is MSG-GEN-01 / MSG-GEN-09 as a choice. Another zone is MSG-GEN-08 either way.
    /// </summary>
    public async Task<(Raft? Raft, AppError? Error)> RaftAsync(
        Guid? raftId, PermissionAction action, string field, bool fromRoute, CancellationToken cancellationToken)
    {
        if (raftId is not { } id || await assignments.GetRaftAsync(id, cancellationToken) is not { } raft)
        {
            return (null, fromRoute ? AppError.NotFound : AppError.Invalid(field));
        }

        if (!await zoneAccess.AllowsAsync(PermissionFunctions.WorkAssignments, action, raft.ZoneId, cancellationToken))
        {
            return (null, AppError.Forbidden);
        }

        if (raft.StatusId != StatusIds.Usage.InUse)
        {
            var status = statusCatalog.DisplayNameOf(raft.StatusId);
            return (null, fromRoute
                ? AppError.Conflict(UserMessages.WrongStatus($"Raft {raft.Code}", status))
                : AppError.NotUsableChoice($"Raft {raft.Code}", status, field));
        }

        return (raft, null);
    }

    public async Task<(JobType? JobType, AppError? Error)> JobTypeAsync(Guid? jobTypeId, CancellationToken cancellationToken)
    {
        if (jobTypeId is not { } id || await assignments.GetJobTypeAsync(id, cancellationToken) is not { } jobType)
        {
            return (null, AppError.Invalid("Job type"));
        }

        return jobType.StatusId == StatusIds.Usage.InUse
            ? (jobType, null)
            : (null, AppError.NotUsableChoice($"Job type {jobType.Name}", statusCatalog.DisplayNameOf(jobType.StatusId), "jobTypeId"));
    }

    /// <summary>Work is assigned for today or a later date (LABOR-02.0.E1), and no pay period holds the date (MSG-LABOR-04).</summary>
    public async Task<AppError?> DateRefusalAsync(DateOnly? date, CancellationToken cancellationToken)
    {
        if (date is not { } day || day < Today())
        {
            return AppError.Invalid("Date", "Work is assigned only for today or a later date.");
        }

        return await payPeriods.CheckAsync(day, cancellationToken);
    }

    /// <summary>The distinct ids of the request, or MSG-GEN-01 when none, too many, or one is empty.</summary>
    public static (IReadOnlyList<Guid>? Ids, AppError? Error) EmployeeIds(IReadOnlyList<Guid>? requested)
    {
        var ids = requested?.Distinct().ToList();
        return ids is not { Count: > 0 and <= MaxEmployees } || ids.Contains(Guid.Empty) ? (null, AppError.Invalid("Employees")) : (ids, null);
    }

    /// <summary>MSG-GEN-09 for an employee who is not Employed, MSG-GEN-01 for a date after their last working day.</summary>
    public AppError? EmployeeRefusal(Employee employee, DateOnly date)
    {
        if (employee.StatusId != StatusIds.Employee.Working)
        {
            return AppError.NotUsableChoice($"Employee {employee.FullName}", statusCatalog.DisplayNameOf(employee.StatusId), "employeeIds");
        }

        return employee.LastWorkingDay is { } last && date > last
            ? AppError.Invalid("Date", $"{employee.FullName} works until {last:dd/MM/yyyy}.")
            : null;
    }

    /// <summary>The attendance of the employees on the date; nobody is checked in until LABOR-04 exists.</summary>
    public Task<IReadOnlyDictionary<Guid, DayAttendance>> AttendanceAsync(DateOnly date, IReadOnlyCollection<Guid> employeeIds, CancellationToken cancellationToken) =>
        attendance.ReadAsync(date, employeeIds, cancellationToken);

    /// <summary>MSG-LABOR-07 (409) naming the first employee; <c>employeeIds</c> lists all of them.</summary>
    public static AppError AlreadyCheckedIn(IReadOnlyList<Employee> checkedIn) => AppError.Conflict(
        UserMessages.AlreadyCheckedIn(checkedIn[0].FullName),
        new Dictionary<string, object?> { ["employeeIds"] = checkedIn.Select(e => e.Id).ToList() });

    /// <summary>The latest job period of the day: the last start time, or the work assigned before the check-in when there is no other.</summary>
    public static WorkAssignment? CurrentPeriod(IEnumerable<WorkAssignment> periodsOfOneEmployee) =>
        periodsOfOneEmployee.OrderByDescending(p => p.StartTime ?? DateTimeOffset.MinValue).FirstOrDefault();
}
