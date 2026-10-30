using System.Security.Cryptography;
using System.Text;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;
namespace OMTS.Application.Features.Workforce;

/// <summary>LABOR-02,04,07,08: record checks use current grants, personal reads derive identity from the session.</summary>
public sealed class WorkforceService(
    IWorkforceRepository repository, IEmployeeRepository employees, IRaftRepository rafts,
    ICurrentUser user, ZoneAccessGuard access, AuditLogService audit, IUnitOfWork uow,
    INotificationDispatcher notifications, TimeProvider clock)
{
    private DateTimeOffset Now => clock.GetUtcNow();
    private DateOnly Today => DateOnly.FromDateTime(Now.ToOffset(TimeSpan.FromHours(7)).DateTime);

    public async Task<Result<WorkforceLookups>> LookupsAsync(DateOnly? day, CancellationToken ct)
    {
        if ((await VisibleZonesAsync(ct)).IsEmpty) return AppError.Forbidden;
        var options = new List<EmployeeOption>();
        foreach (var e in await repository.EmployeesAsync(PermittedZones.Everywhere, ct))
            options.Add(new(e.Id, e.FullName, await repository.HasApprovedLeaveAsync(e.Id, day ?? Today, ct)));
        return new WorkforceLookups((await repository.JobsAsync(ct)).Select(j => new JobOption(j.Id, j.Name)).ToList(), options);
    }

    public async Task<Result<IReadOnlyList<WorkAssignmentView>>> ListAsync(DateOnly day, CancellationToken ct)
    {
        if (day == default) return AppError.Invalid("Work date");
        var zones = await VisibleZonesAsync(ct);
        if (zones.IsEmpty) return AppError.Forbidden;
        var rows = await repository.AssignmentsAsync(day, day, zones, null, ct);
        return Result<IReadOnlyList<WorkAssignmentView>>.Success(await ViewsAsync(rows, ct));
    }

    public async Task<Result<MyWorkforceView>> MineAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        if (from == default || to < from || to.DayNumber - from.DayNumber > 62) return AppError.Invalid("Work date");
        var employee = user.AccountId is { } id ? await repository.MyEmployeeAsync(id, ct) : null;
        if (employee is null) return new MyWorkforceView(null, null, [], [], []);
        var rows = await repository.AssignmentsAsync(from, to, PermittedZones.Everywhere, employee.Id, ct);
        var attendance = await repository.AttendanceHistoryAsync(employee.Id, from, to, ct);
        var settings = await repository.SettingsAsync(ct);
        return new MyWorkforceView(employee.Id, employee.FullName, await ViewsAsync(rows, ct),
            attendance.Select(a => AttendanceCalculator.View(a, settings)).ToList(),
            (await repository.LeaveAsync(employee.Id, ct)).Select(LeaveService.View).ToList());
    }

    public async Task<Result<IReadOnlyList<WorkAssignmentView>>> AssignAsync(AssignWorkRequest input, CancellationToken ct)
    {
        if (input.Date < Today || input.Date == default || input.EmployeeIds is null ||
            input.EmployeeIds.Count is < 1 or > 100 || input.EmployeeIds.Distinct().Count() != input.EmployeeIds.Count)
            return AppError.Invalid("Work assignment");
        var raft = await rafts.GetForEditAsync(input.RaftId, ct);
        if (raft is null) return AppError.NotFound;
        if (!await access.AllowsAsync(PermissionFunctions.WorkAssignments, PermissionAction.Create, raft.ZoneId, ct)) return AppError.Forbidden;
        var job = await repository.JobAsync(input.JobTypeId, ct);
        if (job?.StatusId != StatusIds.Usage.InUse || raft.StatusId != StatusIds.Usage.InUse || raft.Zone.StatusId != StatusIds.Usage.InUse)
            return AppError.Invalid("Job type");
        var changes = new List<(Employee Employee, WorkAssignment? Existing)>();
        foreach (var employeeId in input.EmployeeIds)
        {
            var employee = await employees.GetForEditAsync(employeeId, ct);
            if (employee?.StatusId != StatusIds.Employee.Working) return AppError.Invalid("Employee");
            var attendance = await repository.AttendanceAsync(employeeId, input.Date, ct);
            if (attendance?.CheckedInAt is not null || attendance?.CheckedOutAt is not null) return AppError.Invalid("Attendance already recorded");
            var existing = await repository.EmployeeAssignmentAsync(employeeId, input.Date, ct);
            if (existing is not null)
            {
                if (!await access.AllowsAsync(PermissionFunctions.WorkAssignments, PermissionAction.Edit, existing.ZoneId, ct)) return AppError.Forbidden;
                if (!input.MoveExisting) return AppError.AlreadyExists("Work assignment", existing.Id);
            }
            changes.Add((employee, existing));
        }
        var rows = new List<WorkAssignment>();
        foreach (var (employee, existing) in changes)
        {
            var row = existing ?? new WorkAssignment { EmployeeId = employee.Id, Employee = employee, WorkDate = input.Date, PaymentType = WorkPaymentType.DailyWage };
            var before = new { row.RaftId, row.JobTypeId };
            row.RaftId = raft.Id; row.Raft = raft; row.ZoneId = raft.ZoneId;
            row.JobTypeId = job.Id; row.JobType = job; row.Task = job.Name;
            if (existing is null) repository.AddAssignment(row);
            if (!employee.WorkingZones.Any(z => z.ZoneId == raft.ZoneId))
                employees.AddWorkingZone(new EmployeeWorkingZone { EmployeeId = employee.Id, ZoneId = raft.ZoneId });
            audit.Record(new AuditEntry("WorkAssignment.Saved", nameof(WorkAssignment)) { RecordId = row.Id, ZoneId = raft.ZoneId, Before = before, After = new { row.WorkDate, row.EmployeeId, row.RaftId, row.JobTypeId } });
            if (employee.AccountId is { } accountId)
                await notifications.NotifyAsync([accountId], NotificationTypes.WorkAssignment, "Phân công công việc",
                    $"{input.Date:dd/MM/yyyy}: {job.Name}, bè {raft.Code}", "WorkAssignment", row.Id.ToString(), false, ct);
            rows.Add(row);
        }
        await uow.SaveChangesAsync(ct);
        return Result<IReadOnlyList<WorkAssignmentView>>.Success(await ViewsAsync(rows, ct));
    }

    public async Task<Result<Done>> CancelAsync(Guid id, CancellationToken ct)
    {
        var row = await repository.AssignmentAsync(id, ct);
        if (row is null || row.CancelledAt is not null) return AppError.NotFound;
        if (!await access.AllowsAsync(PermissionFunctions.WorkAssignments, PermissionAction.Delete, row.ZoneId, ct)) return AppError.Forbidden;
        var attendance = await repository.AttendanceAsync(row.EmployeeId!.Value, row.WorkDate, ct);
        if (row.WorkDate < Today || attendance?.CheckedInAt is not null || attendance?.CheckedOutAt is not null) return AppError.Invalid("Attendance already recorded");
        row.CancelledAt = Now; row.CancelledByAccountId = user.AccountId; row.CancellationReason = "Removed before attendance";
        audit.Record(new AuditEntry("WorkAssignment.Cancelled", nameof(WorkAssignment)) { RecordId = row.Id, ZoneId = row.ZoneId });
        if (row.Employee?.AccountId is { } accountId)
            await notifications.NotifyAsync([accountId], NotificationTypes.WorkAssignment, "Đã gỡ phân công", $"{row.WorkDate:dd/MM/yyyy}: {row.Task}", "WorkAssignment", row.Id.ToString(), false, ct);
        await uow.SaveChangesAsync(ct);
        return Done.Value;
    }

    public async Task<Result<WorkAssignmentView>> MarkAsync(Guid assignmentId, bool checkOut, MarkAttendanceRequest input, CancellationToken ct)
    {
        var row = await repository.AssignmentAsync(assignmentId, ct);
        if (row is null || row.CancelledAt is not null || row.EmployeeId is null) return AppError.NotFound;
        if (!await access.AllowsAsync(PermissionFunctions.Attendance, PermissionAction.Create, row.ZoneId, ct)) return AppError.Forbidden;
        if (row.WorkDate != Today || row.Employee?.StatusId != StatusIds.Employee.Working) return AppError.Invalid("Work date");
        if (await repository.HasLockedPayrollPeriodAsync(Today, Today, ct)) return AppError.Invalid("Locked payroll period");
        var now = Now;
        var method = "Manual";
        if (input.QrCode is not null)
        {
            if (input.QrCode.Length > 200) return AppError.Invalid("Attendance QR");
            var qr = await repository.QrAsync(Hash(input.QrCode), ct);
            if (qr is null || qr.EmployeeId != row.EmployeeId || qr.ExpiresAt <= now) return AppError.Invalid("Attendance QR");
            method = "QR";
        }
        var attendance = await repository.AttendanceAsync(row.EmployeeId.Value, Today, ct);
        if (attendance?.ZoneId is { } zone && !await access.AllowsAsync(PermissionFunctions.Attendance, PermissionAction.Create, zone, ct)) return AppError.Forbidden;
        if (checkOut ? attendance?.CheckedOutAt is not null : attendance?.CheckedInAt is not null)
            return AppError.AlreadyExists("Attendance", attendance!.Id);
        if (attendance is null)
        {
            attendance = new AttendanceRecord { EmployeeId = row.EmployeeId, ZoneId = row.ZoneId, WorkDate = Today, RecordedByAccountId = user.AccountId!.Value };
            repository.AddAttendance(attendance);
        }
        // LABOR-04.6: a check-out without a check-in is kept as incomplete, never invented hours.
        if (checkOut)
        {
            if (attendance.CheckedInAt >= now) return AppError.Invalid("Check-out time");
            attendance.CheckedOutAt = now; attendance.CheckOutMethod = method;
        }
        else
        {
            if (attendance.CheckedOutAt is not null || row.JobTypeId is null || row.RaftId is null) return AppError.Invalid("Check-in time");
            attendance.CheckedInAt = now; attendance.CheckInMethod = method;
            repository.AddPeriod(new AttendanceJobPeriod { Attendance = attendance, AttendanceId = attendance.Id,
                JobTypeId = row.JobTypeId.Value, RaftId = row.RaftId.Value, StartsAt = now });
        }
        audit.Record(new AuditEntry(checkOut ? "Attendance.CheckOut" : "Attendance.CheckIn", nameof(AttendanceRecord))
        { RecordId = attendance.Id, ZoneId = row.ZoneId, After = new { attendance.CheckedInAt, attendance.CheckedOutAt, Method = method } });
        await uow.SaveChangesAsync(ct);
        return View(row, attendance);
    }

    public async Task<Result<Done>> ChangeCrewAsync(ChangeCrewRequest input, CancellationToken ct)
    {
        if (await repository.HasLockedPayrollPeriodAsync(Today, Today, ct)) return AppError.Invalid("Locked payroll period");
        if (input.EmployeeIds is null || input.EmployeeIds.Count is < 1 or > 100 || input.EmployeeIds.Distinct().Count() != input.EmployeeIds.Count) return AppError.Invalid("Employee");
        var raft = await rafts.GetForEditAsync(input.RaftId, ct);
        var job = await repository.JobAsync(input.JobTypeId, ct);
        if (raft?.StatusId != StatusIds.Usage.InUse || raft.Zone.StatusId != StatusIds.Usage.InUse || job?.StatusId != StatusIds.Usage.InUse) return AppError.Invalid("Job type");
        if (!await access.AllowsAsync(PermissionFunctions.WorkAssignments, PermissionAction.Edit, raft.ZoneId, ct)) return AppError.Forbidden;
        var changes = new List<(WorkAssignment Row, AttendanceRecord? Attendance)>();
        foreach (var employeeId in input.EmployeeIds)
        {
            var row = await repository.EmployeeAssignmentAsync(employeeId, Today, ct);
            if (row is null) return AppError.NotFound;
            if (row.Employee?.StatusId != StatusIds.Employee.Working) return AppError.Invalid("Employee");
            if (!await access.AllowsAsync(PermissionFunctions.WorkAssignments, PermissionAction.Edit, row.ZoneId, ct)) return AppError.Forbidden;
            var a = await repository.AttendanceAsync(employeeId, Today, ct);
            if (a?.CheckedOutAt is not null) return AppError.Invalid("Attendance already recorded");
            changes.Add((row, a));
        }
        var now = Now;
        if (!changes.Any(c => c.Attendance?.CheckedInAt is not null)) return AppError.Invalid("Crew not checked in");
        foreach (var (row, attendance) in changes)
        {
            var before = new { row.RaftId, row.JobTypeId };
            if (attendance?.CheckedInAt is not null)
            {
                if (attendance.JobPeriods.Any(p => p.StartsAt >= now)) return AppError.Invalid("Job change time");
                repository.AddPeriod(new AttendanceJobPeriod { AttendanceId = attendance.Id, RaftId = raft.Id, JobTypeId = job.Id, StartsAt = now });
                attendance.ZoneId = raft.ZoneId;
            }
            row.RaftId = raft.Id; row.ZoneId = raft.ZoneId; row.JobTypeId = job.Id; row.Task = job.Name;
            var employee = await employees.GetForEditAsync(row.EmployeeId!.Value, ct);
            if (employee is not null && !employee.WorkingZones.Any(z => z.ZoneId == raft.ZoneId))
                employees.AddWorkingZone(new EmployeeWorkingZone { EmployeeId = employee.Id, ZoneId = raft.ZoneId });
            audit.Record(new AuditEntry("WorkAssignment.JobChanged", nameof(WorkAssignment)) { RecordId = row.Id, ZoneId = row.ZoneId, Before = before, After = new { row.RaftId, row.JobTypeId, StartsAt = now } });
        }
        await uow.SaveChangesAsync(ct);
        return Done.Value;
    }

    public async Task<Result<AttendanceQrView>> IssueQrAsync(CancellationToken ct)
    {
        var employee = user.AccountId is { } id ? await repository.MyEmployeeAsync(id, ct) : null;
        if (employee is null) return AppError.Forbidden;
        var code = "omts-attendance:" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var expires = Now.AddMinutes(1);
        var qr = await repository.EmployeeQrAsync(employee.Id, ct);
        if (qr is null) { qr = new AttendanceQr { EmployeeId = employee.Id }; repository.AddQr(qr); }
        qr.TokenHash = Hash(code); qr.ExpiresAt = expires;
        await uow.SaveChangesAsync(ct);
        return new AttendanceQrView(code, expires);
    }

    private async Task<IReadOnlyList<WorkAssignmentView>> ViewsAsync(IReadOnlyList<WorkAssignment> rows, CancellationToken ct)
    {
        var result = new List<WorkAssignmentView>();
        foreach (var row in rows)
            if (row.EmployeeId is { } employeeId) result.Add(View(row, await repository.AttendanceAsync(employeeId, row.WorkDate, ct)));
        return result;
    }
    private static WorkAssignmentView View(WorkAssignment a, AttendanceRecord? attendance) =>
        new(a.Id, a.EmployeeId!.Value, a.Employee?.FullName ?? "", a.ZoneId, a.RaftId, a.Raft?.Code, a.JobTypeId,
            a.JobType?.Name ?? a.Task, a.WorkDate, attendance?.CheckedInAt, attendance?.CheckedOutAt);
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private async Task<PermittedZones> VisibleZonesAsync(CancellationToken ct)
    {
        var work = await access.PermittedZonesAsync(PermissionFunctions.WorkAssignments, PermissionAction.View, ct);
        var attendance = await access.PermittedZonesAsync(PermissionFunctions.Attendance, PermissionAction.View, ct);
        return work.AllZones || attendance.AllZones ? PermittedZones.Everywhere
            : new PermittedZones(false, work.ZoneIds.Concat(attendance.ZoneIds).ToHashSet());
    }
}
