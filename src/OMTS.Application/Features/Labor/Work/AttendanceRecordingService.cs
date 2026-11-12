using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Identity;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Farming;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Labor.Work;

/// <summary>
/// LABOR-04: a Zone Manager records one check-in when work starts at the raft and one check-out when it ends, by the attendance QR code
/// of the employee or by hand, for today (BR-LABOR-06); a Staff employee is checked in from the staff list; the start time of a crew can
/// be entered with a reason. Each time keeps its recording method. A check-out without a check-in is saved and the day stays incomplete.
/// Nothing is recorded in a pay period whose payroll is Awaiting finalization or Finalized (BR-LABOR-13).
/// </summary>
public sealed class AttendanceRecordingService(
    IWorkAssignmentRepository assignments,
    IAttendanceRepository attendances,
    IWorkEmployeeReader employeeReader,
    IEmployeeRequestRepository requests,
    ZoneAccessGuard zones,
    PayPeriodLock payPeriods,
    IAttendanceQrCodes qrCodes,
    IStatusCatalog statusCatalog,
    ICurrentUser currentUser,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    private const int MaxCrew = 100;

    /// <summary>LABOR-04.0: the check-in of the employee of an assignment of today, on the raft of the assignment.</summary>
    public Task<Result<AssignmentView>> CheckInAsync(Guid assignmentId, RecordAttendanceRequest request, CancellationToken cancellationToken) =>
        RecordAssignedAsync(assignmentId, request, isCheckIn: true, cancellationToken);

    /// <summary>LABOR-04.1 and LABOR-04.6: the check-out; without a check-in it is saved and answers MSG-LABOR-12.</summary>
    public Task<Result<AssignmentView>> CheckOutAsync(Guid assignmentId, RecordAttendanceRequest request, CancellationToken cancellationToken) =>
        RecordAssignedAsync(assignmentId, request, isCheckIn: false, cancellationToken);

    /// <summary>LABOR-04.7: the check-in of a Staff employee, who needs no work assignment.</summary>
    public Task<Result<StaffAttendanceView>> StaffCheckInAsync(Guid employeeId, RecordAttendanceRequest request, CancellationToken cancellationToken) =>
        RecordStaffAsync(employeeId, request, isCheckIn: true, cancellationToken);

    public Task<Result<StaffAttendanceView>> StaffCheckOutAsync(Guid employeeId, RecordAttendanceRequest request, CancellationToken cancellationToken) =>
        RecordStaffAsync(employeeId, request, isCheckIn: false, cancellationToken);

    private async Task<Result<AssignmentView>> RecordAssignedAsync(Guid assignmentId, RecordAttendanceRequest request, bool isCheckIn, CancellationToken cancellationToken)
    {
        var assignment = await assignments.GetAsync(assignmentId, cancellationToken);
        if (assignment is null)
        {
            return AppError.NotFound;
        }

        if (!await zones.AllowsAsync(PermissionFunctions.Attendance, PermissionAction.Create, assignment.Raft.ZoneId, cancellationToken))
        {
            return AppError.Forbidden;
        }

        var today = timeProvider.Today();
        if (assignment.Date != today)
        {
            return AppError.Invalid("Date (today only)");
        }

        var employee = (await employeeReader.GetForWorkAsync([assignment.EmployeeId], cancellationToken))[0];
        var recorded = await RecordAsync(employee, today, isCheckIn, request, assignment.Raft.ZoneId, cancellationToken);
        if (!recorded.IsSuccess)
        {
            return recorded.Error!;
        }

        var row = (await RowsAsync([employee.Id], today, cancellationToken))[0];
        return row with { Message = recorded.Value!.Message.Text, MessageCode = recorded.Value.Message.Code };
    }

    private async Task<Result<StaffAttendanceView>> RecordStaffAsync(Guid employeeId, RecordAttendanceRequest request, bool isCheckIn, CancellationToken cancellationToken)
    {
        var employee = (await employeeReader.GetForWorkAsync([employeeId], cancellationToken)).FirstOrDefault();
        if (employee is null)
        {
            return AppError.NotFound;
        }

        var permitted = await zones.PermittedZonesAsync(PermissionFunctions.Attendance, PermissionAction.Create, cancellationToken);
        var zone = employee.WorkingZones.Select(w => w.ZoneId).FirstOrDefault(permitted.Allows);
        if (zone == Guid.Empty && !permitted.AllZones)
        {
            return AppError.Forbidden;
        }

        var today = timeProvider.Today();
        // A seasonal worker is checked in only with work assigned that day, because the job type sets the pay (BR-LABOR-06).
        if (WorkRules.TypeOn(employee, today) != EmployeeType.Staff)
        {
            return AppError.Invalid("Employee");
        }

        var recorded = await RecordAsync(employee, today, isCheckIn, request, zone == Guid.Empty ? null : zone, cancellationToken);
        if (!recorded.IsSuccess)
        {
            return recorded.Error!;
        }

        var record = recorded.Value!.Attendance;
        var leave = (await requests.ListApprovedLeaveAsync([employee.Id], today, cancellationToken)).Count > 0;
        return new StaffAttendanceView(employee.Id, employee.FullName, employee.Code, today, record.CheckInAt, record.CheckOutAt, record.IsComplete, leave)
        {
            Message = recorded.Value.Message.Text, MessageCode = recorded.Value.Message.Code,
        };
    }

    /// <summary>
    /// LABOR-04.8: the check-in of the selected members of a raft's crew at the start time entered, with the reason, because the Zone
    /// Manager was not at the raft when it started. All or none.
    /// </summary>
    public async Task<Result<IReadOnlyList<AssignmentView>>> CrewStartAsync(CrewStartRequest request, CancellationToken cancellationToken)
    {
        var today = timeProvider.Today();
        var now = WorkRules.Now(timeProvider);
        var check = new FieldCheck();
        check.Check(request.RaftId is { } r && r != Guid.Empty, "Raft");
        var reason = check.Required(request.Reason, "Reason", WorkRules.MaxReason);
        var startsAt = WorkRules.ParseTime(request.StartTime) is { } time ? WorkRules.At(today, time) : (DateTimeOffset?)null;
        check.Check(startsAt is { } s && s <= now, "Start time");
        var ids = request.EmployeeIds ?? [];
        check.Check(ids.Count is >= 1 and <= MaxCrew && ids.Distinct().Count() == ids.Count && ids.All(i => i != Guid.Empty), "Employees");
        if (!check.IsValid)
        {
            return AppError.InvalidFields(check.Invalid);
        }

        var raftId = request.RaftId!.Value;
        var crew = await assignments.ListOfEmployeesAsync(ids, today, cancellationToken);
        var onRaft = crew.GroupBy(p => p.EmployeeId)
            .Where(g => g.OrderBy(p => p.StartsAt is null ? 0 : 1).ThenBy(p => p.StartsAt).Last().RaftId == raftId)
            .Select(g => g.Key).ToHashSet();
        if (ids.Any(i => !onRaft.Contains(i)))
        {
            return AppError.Invalid("Employees");
        }

        var zoneId = crew.First(p => p.RaftId == raftId).Raft.ZoneId;
        if (!await zones.AllowsAsync(PermissionFunctions.Attendance, PermissionAction.Create, zoneId, cancellationToken))
        {
            return AppError.Forbidden;
        }

        var people = await employeeReader.GetForWorkAsync(ids, cancellationToken);
        foreach (var employee in people.OrderBy(e => e.FullName))
        {
            var recorded = await RecordAsync(
                employee, today, isCheckIn: true, new RecordAttendanceRequest(null), zoneId, cancellationToken,
                at: startsAt, method: AttendanceMethod.CrewStart, reason: reason, save: false);
            if (!recorded.IsSuccess)
            {
                return recorded.Error!;
            }
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result<IReadOnlyList<AssignmentView>>.Success(await RowsAsync(ids, today, cancellationToken));
    }

    /// <summary>
    /// The shared rules of one check-in or check-out of <paramref name="day"/> (today): the employee is Employed, the pay period is not
    /// locked, a QR code is the employee's and current, the time is not recorded already and keeps the two in order.
    /// </summary>
    private async Task<Result<RecordedTime>> RecordAsync(
        Employee employee, DateOnly day, bool isCheckIn, RecordAttendanceRequest request, Guid? zoneId, CancellationToken cancellationToken,
        DateTimeOffset? at = null, AttendanceMethod? method = null, string? reason = null, bool save = true)
    {
        if (!WorkRules.IsEmployedOn(employee, day))
        {
            return AppError.NotUsableChoice($"Employee {employee.FullName}", statusCatalog.DisplayNameOf(StatusIds.Employee.Left), "Employee");
        }

        if (method is null)
        {
            if (string.IsNullOrWhiteSpace(request.QrCode))
            {
                method = AttendanceMethod.Manual;
            }
            else if (qrCodes.Read(request.QrCode) == employee.Id)
            {
                method = AttendanceMethod.QrCode;
            }
            else
            {
                return new AppError(ErrorKind.Validation, WorkMessages.QrCodeNotRecognized, new Dictionary<string, object?> { ["field"] = "QR code" });
            }
        }

        if (await payPeriods.CheckAsync(day, cancellationToken) is { } locked)
        {
            return locked;
        }

        var time = at ?? WorkRules.Now(timeProvider);
        var record = await attendances.GetAsync(employee.Id, day, cancellationToken);
        var account = currentUser.AccountId ?? throw new InvalidOperationException("Attendance is recorded by a logged-in user.");
        if (isCheckIn && record?.CheckInAt is { } checkIn)
        {
            return Recorded(employee, "check-in", checkIn);
        }

        if (!isCheckIn && record?.CheckOutAt is { } checkOut)
        {
            return Recorded(employee, "check-out", checkOut);
        }

        // The two times stay in order: a check-in after a check-out, or the other way round, is not what happened.
        if (isCheckIn ? record?.CheckOutAt <= time : record?.CheckInAt > time)
        {
            return AppError.Invalid(isCheckIn ? "Check-in" : "Check-out");
        }

        var before = record is null ? null : Snapshot(record);
        if (record is null)
        {
            record = new Attendance { EmployeeId = employee.Id, Date = day };
            attendances.Add(record);
        }

        if (isCheckIn)
        {
            (record.CheckInAt, record.CheckInMethod, record.CheckInRecordedByAccountId, record.CheckInReason) = (time, method, account, reason);
        }
        else
        {
            (record.CheckOutAt, record.CheckOutMethod, record.CheckOutRecordedByAccountId, record.CheckOutReason) = (time, method, account, reason);
        }

        audit.Record(new AuditEntry(isCheckIn ? "Attendance.CheckedIn" : "Attendance.CheckedOut", nameof(Attendance))
        {
            RecordId = record.Id, RecordCode = employee.Code, ZoneId = zoneId, Before = before, After = Snapshot(record), Reason = reason,
        });
        if (save)
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        var name = isCheckIn ? "Check-in" : "Check-out";
        var message = !isCheckIn && record.CheckInAt is null
            ? WorkMessages.CheckOutWithoutCheckIn(employee.FullName, time)
            : UserMessages.Saved($"{name} of {employee.FullName} at {BusinessClock.Format(time)[..5]}");
        return new RecordedTime(record, message);
    }

    private static AppError Recorded(Employee employee, string what, DateTimeOffset time) => AppError.Conflict(
        WorkMessages.AlreadyRecorded(employee.FullName, what, time), new Dictionary<string, object?> { ["employeeId"] = employee.Id });

    private async Task<IReadOnlyList<AssignmentView>> RowsAsync(IReadOnlyCollection<Guid> employeeIds, DateOnly day, CancellationToken cancellationToken)
    {
        var periods = await assignments.ListOfEmployeesAsync(employeeIds, day, cancellationToken);
        var attended = (await attendances.ListOfEmployeesAsync(employeeIds, day, cancellationToken)).ToDictionary(a => a.EmployeeId);
        var leave = (await requests.ListApprovedLeaveAsync(employeeIds, day, cancellationToken)).Select(l => l.EmployeeId).ToHashSet();
        return WorkRules.Rows(periods, attended, leave);
    }

    private static object Snapshot(Attendance a) => new { a.Date, a.CheckInAt, a.CheckOutAt, a.CheckInMethod, a.CheckOutMethod };

    private sealed record RecordedTime(Attendance Attendance, UserMessage Message);
}
