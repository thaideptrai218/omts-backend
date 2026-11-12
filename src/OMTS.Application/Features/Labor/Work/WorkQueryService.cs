using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Identity;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Labor.Work;

/// <summary>
/// What the work screens read: the day board and its pickers (SCR-LABOR-02), the staff list, the requests (SCR-LABOR-03), and the own work
/// of the employee linked to the caller's account (LABOR-08, LABOR-07). The zones the caller sees follow BR-FARM-08: a work assignment
/// and its attendance follow the zone of the raft, a Staff employee's attendance the working zones of the employee, and employee requests
/// have no zone.
/// </summary>
public sealed class WorkQueryService(
    IWorkAssignmentRepository assignments,
    IAttendanceRepository attendances,
    IAttendanceSettingsRepository settings,
    IEmployeeRequestRepository requests,
    IEmployeeRepository employees,
    IWorkEmployeeReader employeeReader,
    JobTypeQueryService jobTypes,
    IAttendanceQrCodes qrCodes,
    ZoneAccessGuard zones,
    IStatusCatalog statusCatalog,
    ICurrentUser currentUser,
    TimeProvider timeProvider)
{
    /// <summary>The longest range of My work (about two pay periods).</summary>
    public const int MaxMyWorkDays = 62;

    private const int MaxRequests = 500;

    /// <summary>LABOR-02.0 step 1: every employee with work on the day on the rafts of the caller's zones, with the check-in and check-out.</summary>
    public async Task<Result<IReadOnlyList<AssignmentView>>> ListAssignmentsAsync(DateOnly day, CancellationToken cancellationToken)
    {
        var permitted = await zones.PermittedZonesAsync(PermissionFunctions.WorkAssignments, PermissionAction.View, cancellationToken);
        var periods = (await assignments.ListDayAsync(day, cancellationToken)).Where(p => permitted.Allows(p.Raft.ZoneId)).ToList();
        var ids = periods.Select(p => p.EmployeeId).Distinct().ToList();
        var attended = (await attendances.ListOfEmployeesAsync(ids, day, cancellationToken)).ToDictionary(a => a.EmployeeId);
        var leave = (await requests.ListApprovedLeaveAsync(ids, day, cancellationToken)).Select(l => l.EmployeeId).ToHashSet();
        return Result<IReadOnlyList<AssignmentView>>.Success(WorkRules.Rows(periods, attended, leave));
    }

    /// <summary>LABOR-02 steps 3 and 5: the job types In Use, and the Employed employees with a working zone among the caller's zones, with their leave and assignment of the day.</summary>
    public async Task<WorkLookups> LookupsAsync(DateOnly day, CancellationToken cancellationToken)
    {
        var permitted = await zones.PermittedZonesAsync(PermissionFunctions.WorkAssignments, PermissionAction.View, cancellationToken);
        var jobs = (await jobTypes.OptionsAsync(cancellationToken)).Select(j => new WorkJobOption(j.Id, j.Name)).ToList();
        if (permitted.IsEmpty)
        {
            return new WorkLookups(jobs, []);
        }

        var people = await employeeReader.ListEmployedAsync(permitted.AllZones ? null : permitted.ZoneIds.ToList(), day, cancellationToken);
        var ids = people.Select(e => e.Id).ToList();
        var leave = (await requests.ListApprovedLeaveAsync(ids, day, cancellationToken)).Select(l => l.EmployeeId).ToHashSet();
        var raftOf = (await assignments.ListDayAsync(day, cancellationToken))
            .GroupBy(p => p.EmployeeId)
            .ToDictionary(g => g.Key, g => g.OrderBy(p => p.StartsAt is null ? 0 : 1).ThenBy(p => p.StartsAt).Last().Raft.Code);
        var checkedIn = (await attendances.ListDayAsync(day, cancellationToken)).Where(a => a.CheckInAt is not null).Select(a => a.EmployeeId).ToHashSet();
        var options = people.Select(e => new WorkEmployeeOption(e.Id, e.FullName, leave.Contains(e.Id))
        {
            Code = e.Code,
            PhoneNumber = e.PhoneNumber,
            Type = WorkRules.TypeOn(e, day),
            JobTypeIds = e.JobTypes.Select(j => j.JobTypeId).ToList(),
            RaftCode = raftOf.GetValueOrDefault(e.Id),
            CheckedIn = checkedIn.Contains(e.Id),
        }).ToList();
        return new WorkLookups(jobs, options);
    }

    /// <summary>LABOR-04.7: the Staff employees of the caller's zones with their check-in and check-out on the day.</summary>
    public async Task<IReadOnlyList<StaffAttendanceView>> StaffAsync(DateOnly day, CancellationToken cancellationToken)
    {
        var permitted = await zones.PermittedZonesAsync(PermissionFunctions.Attendance, PermissionAction.View, cancellationToken);
        if (permitted.IsEmpty)
        {
            return [];
        }

        var people = (await employeeReader.ListEmployedAsync(permitted.AllZones ? null : permitted.ZoneIds.ToList(), day, cancellationToken))
            .Where(e => WorkRules.TypeOn(e, day) == EmployeeType.Staff).ToList();
        var ids = people.Select(e => e.Id).ToList();
        var attended = (await attendances.ListDayAsync(day, cancellationToken)).ToDictionary(a => a.EmployeeId);
        var leave = (await requests.ListApprovedLeaveAsync(ids, day, cancellationToken)).Select(l => l.EmployeeId).ToHashSet();
        return people.Select(e =>
        {
            attended.TryGetValue(e.Id, out var record);
            return new StaffAttendanceView(e.Id, e.FullName, e.Code, day, record?.CheckInAt, record?.CheckOutAt, record?.IsComplete == true, leave.Contains(e.Id));
        }).ToList();
    }

    /// <summary>SCR-LABOR-03: the requests of every employee, Awaiting review first and oldest first, then the others; <paramref name="statusId"/> filters one status.</summary>
    public async Task<Result<IReadOnlyList<EmployeeRequestView>>> ListRequestsAsync(int? statusId, CancellationToken cancellationToken)
    {
        if (statusId is { } id && !EmployeeRequestStatuses.Contains(id))
        {
            return AppError.Invalid("Status");
        }

        var rows = await requests.ListAsync(statusId, MaxRequests, cancellationToken);
        return Result<IReadOnlyList<EmployeeRequestView>>.Success(rows.Select(r => EmployeeRequestViews.Of(r, statusCatalog)).ToList());
    }

    /// <summary>
    /// LABOR-08: the own assignments, attendance days with the hours of each job type, and requests of the employee linked to the caller's
    /// account, from <paramref name="from"/> to <paramref name="to"/> (at most 62 days). An account with no employee profile sees nothing.
    /// </summary>
    public async Task<Result<MyWork>> MyWorkAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        if (to < from || to.DayNumber - from.DayNumber >= MaxMyWorkDays)
        {
            return AppError.Invalid("Period");
        }

        var employee = await OwnEmployeeAsync(cancellationToken);
        if (employee is null)
        {
            return new MyWork(null, null, [], [], []);
        }

        var periods = await assignments.ListOfEmployeeAsync(employee.Id, from, to, cancellationToken);
        var records = await attendances.ListOfEmployeeAsync(employee.Id, from, to, cancellationToken);
        var versions = await settings.ListAsync(cancellationToken);
        var detail = (await employeeReader.GetForWorkAsync([employee.Id], cancellationToken))[0];
        var rows = periods.GroupBy(p => p.Date)
            .SelectMany(g => WorkRules.Rows(g, records.Where(r => r.Date == g.Key).ToDictionary(r => r.EmployeeId), new HashSet<Guid>()))
            .OrderBy(r => r.Date).ToList();
        var days = records.Select(record => Day(detail, record, periods.Where(p => p.Date == record.Date).ToList(), versions)).ToList();
        var mine = (await requests.ListOfEmployeeAsync(employee.Id, from, to, cancellationToken)).Select(r => EmployeeRequestViews.Of(r, statusCatalog)).ToList();
        return new MyWork(employee.Id, employee.FullName, rows, days, mine);
    }

    /// <summary>LABOR-07: the QR code the employee app shows; it is replaced every minute. Needs an employee profile linked to the account.</summary>
    public async Task<Result<MyAttendanceQr>> MyQrCodeAsync(CancellationToken cancellationToken)
    {
        var employee = await OwnEmployeeAsync(cancellationToken);
        if (employee is null)
        {
            return AppError.Forbidden;
        }

        if (!WorkRules.IsEmployedOn(employee, timeProvider.Today()))
        {
            return AppError.Conflict(Common.Messages.UserMessages.WrongStatus("Employee", statusCatalog.DisplayNameOf(employee.StatusId)));
        }

        var code = qrCodes.Issue(employee.Id);
        return new MyAttendanceQr(code.Code, code.ExpiresAt, (int)IAttendanceQrCodes.Lifetime.TotalSeconds);
    }

    /// <summary>A day of the employee: with hours for a seasonal worker once complete, one day worked for Staff (BR-LABOR-05, BR-LABOR-06).</summary>
    private static MyAttendanceDayView Day(
        Domain.Entities.Identity.Employee employee, Attendance record, IReadOnlyList<WorkAssignment> periods, IReadOnlyList<AttendanceSettingsVersion> versions)
    {
        if (WorkRules.TypeOn(employee, record.Date) == EmployeeType.Staff || !record.IsComplete)
        {
            return new MyAttendanceDayView(record.Date, record.CheckInAt, record.CheckOutAt, [], [], record.IsComplete);
        }

        var hours = WorkHoursCalculator.Calculate(record.CheckInAt, record.CheckOutAt, periods, AttendanceSettingsVersion.EffectiveOn(versions, record.Date));
        var names = periods.GroupBy(p => p.JobTypeId).ToDictionary(g => g.Key, g => g.First().JobType.Name);
        var rafts = periods.GroupBy(p => p.RaftId).ToDictionary(g => g.Key, g => g.First().Raft.Code);
        return new MyAttendanceDayView(
            record.Date, record.CheckInAt, record.CheckOutAt,
            hours.Periods.Select(p => new MyPeriodView(p.JobTypeId, names[p.JobTypeId], rafts[p.RaftId], p.StartsAt, p.EndsAt, p.Minutes)).ToList(),
            hours.Jobs.Select(j => new MyJobHoursView(j.JobTypeId, names[j.JobTypeId], j.Hours)).ToList(),
            true);
    }

    private async Task<Domain.Entities.Identity.Employee?> OwnEmployeeAsync(CancellationToken cancellationToken) =>
        currentUser.AccountId is { } accountId ? await employees.GetByAccountIdAsync(accountId, cancellationToken) : null;

    private static readonly HashSet<int> EmployeeRequestStatuses =
    [
        Domain.Constants.StatusIds.EmployeeRequest.AwaitingReview, Domain.Constants.StatusIds.EmployeeRequest.Approved,
        Domain.Constants.StatusIds.EmployeeRequest.Rejected, Domain.Constants.StatusIds.EmployeeRequest.Cancelled,
    ];
}
