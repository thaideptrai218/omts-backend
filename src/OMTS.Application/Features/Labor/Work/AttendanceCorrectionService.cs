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
using OMTS.Application.Features.Farming;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Labor.Work;

/// <summary>
/// LABOR-04.4: the Cooperative Owner, or a Zone Manager in the zone of the day (ATTENDANCE Edit; owner decision of 20/11/2026), enters a missing or corrected check-in or check-out of an employee and day, today or earlier, and
/// changes, adds or removes the job periods of the day, with a reason; the times and the periods then have the method Correction and
/// the hours are worked out again when shown (BR-LABOR-05). The audit log keeps the values before and after with the reason. Nothing
/// can be corrected in a pay period whose payroll is Finalized (BR-LABOR-13, MSG-LABOR-04). The client asks
/// MSG-GEN-04 before it sends.
/// </summary>
public sealed class AttendanceCorrectionService(
    IWorkAssignmentRepository assignments,
    IAttendanceRepository attendances,
    IAttendanceSettingsRepository settings,
    IWorkEmployeeReader employeeReader,
    IRaftRepository rafts,
    ZoneAccessGuard zones,
    PayPeriodLock payPeriods,
    RowVersionGuard rowVersions,
    IStatusCatalog statusCatalog,
    ICurrentUser currentUser,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    /// <summary>The attendance record and the job periods of an employee on a day, as the correction form shows them. Cooperative Owner, or a Zone Manager of the zone of the day (ATTENDANCE Edit).</summary>
    public async Task<Result<AttendanceDayView>> GetDayAsync(Guid employeeId, DateOnly day, CancellationToken cancellationToken)
    {
        var employee = (await employeeReader.GetForWorkAsync([employeeId], cancellationToken)).FirstOrDefault();
        if (employee is null)
        {
            return await zones.IsCooperativeOwnerAsync(cancellationToken) ? AppError.NotFound : AppError.Forbidden;
        }

        var periods = await assignments.ListOfEmployeesAsync([employee.Id], day, cancellationToken);
        if (!await MayCorrectAsync(employee, periods.Select(p => p.ZoneId), cancellationToken))
        {
            return AppError.Forbidden;
        }

        return await ViewAsync(employee, day, cancellationToken);
    }

    public async Task<Result<AttendanceCorrected>> CorrectAsync(Guid employeeId, DateOnly day, CorrectAttendanceRequest request, CancellationToken cancellationToken)
    {
        var employee = (await employeeReader.GetForWorkAsync([employeeId], cancellationToken)).FirstOrDefault();
        if (employee is null)
        {
            return await zones.IsCooperativeOwnerAsync(cancellationToken) ? AppError.NotFound : AppError.Forbidden;
        }

        // The zone of the day is that of its job periods (a Staff employee, who has none: a working zone of the employee); a Zone Manager needs ATTENDANCE Edit in it (decision of 20/11/2026), the Owner any.
        var existing = (await assignments.ListOfEmployeesAsync([employee.Id], day, cancellationToken))
            .OrderBy(p => p.StartsAt is null ? 0 : 1).ThenBy(p => p.StartsAt).ToList();
        if (!await MayCorrectAsync(employee, existing.Select(p => p.ZoneId), cancellationToken))
        {
            return AppError.Forbidden;
        }

        var check = new FieldCheck();
        check.Check(day <= timeProvider.Today(), "Date");
        var reason = check.Required(request.Reason, "Reason", WorkRules.MaxReason);
        var inputs = (request.Periods ?? []).OrderBy(p => p.StartsAt is null ? 0 : 1).ThenBy(p => p.StartsAt).ToList();
        Validate(check, employee, day, request, inputs);
        if (!check.IsValid)
        {
            return AppError.InvalidFields(check.Invalid);
        }

        if (await payPeriods.CheckAsync(day, cancellationToken) is { } locked)
        {
            return locked;
        }

        var record = await attendances.GetAsync(employee.Id, day, cancellationToken);
        // A screen of a day with no record carries no version. When a record exists now (the first check-in came after the screen was read) the screen is stale: MSG06, not an invalid version (b1b of the
        // review of 13/11/2026). The in-memory provider issues no versions, so there an empty one still matches.
        if (record is { RowVersion.Length: > 0 } && string.IsNullOrWhiteSpace(request.RowVersion))
        {
            return AppError.Conflict(UserMessages.ConcurrentUpdateByAnotherUser);
        }

        if (record is not null && await rowVersions.CheckAsync(record, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        var (refused, zoneOfRaft) = await RefuseChoicesAsync(inputs, existing, cancellationToken);
        if (refused is not null)
        {
            return refused;
        }

        // The zones the corrected periods move work into must be the caller's too: a Zone Manager cannot put an employee on a raft of another zone.
        var newZones = zoneOfRaft.Values.Concat(inputs.Where(p => p.RaftId is null).Select(p => p.ZoneId!.Value));
        if (!await AllowsEveryZoneAsync(newZones, cancellationToken))
        {
            return AppError.Forbidden;
        }

        var account = currentUser.AccountId ?? throw new InvalidOperationException("Attendance is corrected by a logged-in user.");
        var before = Snapshot(record, existing);
        record = ApplyTimes(employee, day, record, request, reason, account);
        ApplyPeriods(employee, day, existing, inputs, zoneOfRaft, reason, account, request.CheckInAt, request.CheckOutAt);
        audit.Record(new AuditEntry("Attendance.Corrected", nameof(Attendance))
        {
            RecordId = record.Id, RecordCode = employee.Code, Before = before, After = Snapshot(record, inputs), Reason = reason,
        });
        await unitOfWork.SaveChangesAsync(cancellationToken);

        var saved = UserMessages.Saved("Attendance");
        return new AttendanceCorrected(await ViewAsync(employee, day, cancellationToken), saved.Text, saved.Code);
    }

    /// <summary>
    /// Whether the caller may correct the day of this employee: the Owner always; a Zone Manager when it holds ATTENDANCE Edit in every zone of the day's job periods, or, when the day has none (a Staff
    /// employee, or a day not yet assigned), in a working zone of the employee.
    /// </summary>
    private async Task<bool> MayCorrectAsync(Employee employee, IEnumerable<Guid> periodZoneIds, CancellationToken cancellationToken)
    {
        if (await zones.IsCooperativeOwnerAsync(cancellationToken))
        {
            return true;
        }

        var periodZones = periodZoneIds.Distinct().ToList();
        if (periodZones.Count > 0)
        {
            return await AllowsEveryZoneAsync(periodZones, cancellationToken);
        }

        foreach (var working in employee.WorkingZones.Select(w => w.ZoneId))
        {
            if (await zones.AllowsAsync(PermissionFunctions.Attendance, PermissionAction.Edit, working, cancellationToken))
            {
                return true;
            }
        }

        // No working zone (a seasonal worker has no fixed one): any holder of ATTENDANCE Edit in some zone may correct the day.
        return employee.WorkingZones.Count == 0 && !(await zones.PermittedZonesAsync(PermissionFunctions.Attendance, PermissionAction.Edit, cancellationToken)).IsEmpty;
    }

    private async Task<bool> AllowsEveryZoneAsync(IEnumerable<Guid> zoneIds, CancellationToken cancellationToken)
    {
        foreach (var zoneId in zoneIds.Distinct())
        {
            if (!await zones.AllowsAsync(PermissionFunctions.Attendance, PermissionAction.Edit, zoneId, cancellationToken))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The checks of LABOR-04.4.E2: the times are on the day and in order, and each period of a seasonal worker has a job type and a raft and starts between them.</summary>
    private static void Validate(FieldCheck check, Employee employee, DateOnly day, CorrectAttendanceRequest request, IReadOnlyList<DayPeriodInput> inputs)
    {
        check.Check(request.CheckInAt is null || BusinessClock.ToBusinessDate(request.CheckInAt.Value) == day, "Check-in");
        // A night shift ends on the next day (L2): the check-out is on the day of the check-in or the next, no later than 18 hours after the check-in; the day worked stays the day of the check-in.
        check.Check(
            request.CheckOutAt is null
            || (BusinessClock.ToBusinessDate(request.CheckOutAt.Value) is var outDay && (outDay == day || outDay == day.AddDays(1))
                && (request.CheckInAt is not { } inAt || request.CheckOutAt.Value - inAt <= WorkRules.MaxShift)),
            "Check-out");
        check.Check(request.CheckInAt is null || request.CheckOutAt is null || request.CheckOutAt > request.CheckInAt, "Check-out");
        check.Check(request.CheckInAt is not null || request.CheckOutAt is not null, "Check-in");
        if (WorkRules.TypeOn(employee, day) == EmployeeType.Staff)
        {
            // A Staff employee has no raft and no job period (BR-LABOR-06).
            check.Check(inputs.Count == 0, "Job periods");
            return;
        }

        check.Check(inputs.Count >= 1, "Job periods");
        check.Check(inputs.All(p => p.JobTypeId is { } j && j != Guid.Empty), "Job type");
        // A period is on a raft, or in a zone the day already has when it has no raft (nursery).
        check.Check(inputs.All(p => p.RaftId is { } r ? r != Guid.Empty : p.ZoneId is { } z && z != Guid.Empty), "Raft");
        check.Check(inputs.Count(p => p.StartsAt is null) <= 1, "Job periods");
        check.Check(inputs.Count == 0 || inputs[0].StartsAt is null, "Job periods");
        var starts = inputs.Where(p => p.StartsAt is not null).Select(p => p.StartsAt!.Value).ToList();
        check.Check(starts.Distinct().Count() == starts.Count, "Job periods");
        check.Check(
            starts.All(s => request.CheckInAt is { } i && s > i && (request.CheckOutAt is not { } o || s < o)),
            "Job periods");
    }

    /// <summary>A job type or raft that is new on the day must be In Use (GBR-01); one the day already has stays, as an assignment keeps it.</summary>
    private async Task<(AppError? Refused, Dictionary<Guid, Guid> ZoneOfRaft)> RefuseChoicesAsync(
        IReadOnlyList<DayPeriodInput> inputs, IReadOnlyList<WorkAssignment> existing, CancellationToken cancellationToken)
    {
        var zoneOfRaft = new Dictionary<Guid, Guid>();
        var jobTypeIds = inputs.Select(p => p.JobTypeId!.Value).Distinct().ToList();
        var jobTypes = (await assignments.GetJobTypesAsync(jobTypeIds, cancellationToken)).ToDictionary(j => j.Id);
        foreach (var id in jobTypeIds)
        {
            if (!jobTypes.TryGetValue(id, out var jobType))
            {
                return (AppError.Invalid("Job type"), zoneOfRaft);
            }

            if (jobType.StatusId != StatusIds.Usage.InUse && existing.All(p => p.JobTypeId != id))
            {
                return (AppError.NotUsableChoice($"Job type {jobType.Name}", statusCatalog.DisplayNameOf(jobType.StatusId), "Job type"), zoneOfRaft);
            }
        }

        foreach (var zone in inputs.Where(p => p.RaftId is null).Select(p => p.ZoneId!.Value).Distinct())
        {
            if (existing.All(p => p.RaftId is not null || p.ZoneId != zone))
            {
                return (AppError.Invalid("Raft"), zoneOfRaft);
            }
        }

        var raftIds = inputs.Where(p => p.RaftId is not null).Select(p => p.RaftId!.Value).Distinct().ToList();
        var found = (await rafts.GetByIdsAsync(raftIds, cancellationToken)).ToDictionary(r => r.Id);
        foreach (var id in raftIds)
        {
            if (!found.TryGetValue(id, out var raft))
            {
                return (AppError.Invalid("Raft"), zoneOfRaft);
            }

            if (raft.StatusId != StatusIds.Usage.InUse && existing.All(p => p.RaftId != id))
            {
                return (AppError.NotUsableChoice($"Raft {raft.Code}", statusCatalog.DisplayNameOf(raft.StatusId), "Raft"), zoneOfRaft);
            }

            zoneOfRaft[id] = raft.ZoneId;
        }

        return (null, zoneOfRaft);
    }

    private Attendance ApplyTimes(Employee employee, DateOnly day, Attendance? record, CorrectAttendanceRequest request, string reason, Guid account)
    {
        if (record is null)
        {
            record = new Attendance { EmployeeId = employee.Id, Date = day };
            attendances.Add(record);
        }

        if (record.CheckInAt != request.CheckInAt)
        {
            (record.CheckInAt, record.CheckInMethod, record.CheckInRecordedByAccountId, record.CheckInReason) =
                (request.CheckInAt, request.CheckInAt is null ? null : AttendanceMethod.Correction, account, reason);
        }

        if (record.CheckOutAt != request.CheckOutAt)
        {
            (record.CheckOutAt, record.CheckOutMethod, record.CheckOutRecordedByAccountId, record.CheckOutReason) =
                (request.CheckOutAt, request.CheckOutAt is null ? null : AttendanceMethod.Correction, account, reason);
        }

        return record;
    }

    /// <summary>The periods are matched in time order: a period that changes is marked Correction with the reason, extra ones are added and surplus ones removed.</summary>
    private void ApplyPeriods(
        Employee employee, DateOnly day, IReadOnlyList<WorkAssignment> existing, IReadOnlyList<DayPeriodInput> inputs, IReadOnlyDictionary<Guid, Guid> zoneOfRaft, string reason, Guid account,
        DateTimeOffset? checkIn, DateTimeOffset? checkOut)
    {
        var result = new List<WorkAssignment>();
        for (var i = 0; i < inputs.Count; i++)
        {
            var input = inputs[i];
            var zoneId = input.RaftId is { } raftOfInput ? zoneOfRaft[raftOfInput] : input.ZoneId!.Value;
            if (i >= existing.Count)
            {
                var added = new WorkAssignment
                {
                    EmployeeId = employee.Id, Date = day, RaftId = input.RaftId, ZoneId = zoneId, JobTypeId = input.JobTypeId!.Value, StartsAt = input.StartsAt,
                    AssignedByAccountId = account, Method = AttendanceMethod.Correction, CorrectionReason = reason,
                };
                assignments.Add(added);
                result.Add(added);
                continue;
            }

            var period = existing[i];
            result.Add(period);
            if (period.RaftId == input.RaftId && period.ZoneId == zoneId && period.JobTypeId == input.JobTypeId && period.StartsAt == input.StartsAt)
            {
                continue;
            }

            (period.RaftId, period.ZoneId, period.JobTypeId, period.StartsAt, period.Method, period.CorrectionReason) =
                (input.RaftId, zoneId, input.JobTypeId!.Value, input.StartsAt, AttendanceMethod.Correction, reason);
        }

        foreach (var surplus in existing.Skip(inputs.Count))
        {
            assignments.Remove(surplus);
        }

        KeepEndsConsistent(result, checkIn, checkOut);
    }

    /// <summary>
    /// A period closed at its own check-out (<see cref="WorkAssignment.EndsAt"/>, a day worked in two sessions) keeps that end only while it still lies after the period's start, before the next period
    /// starts and not after the check-out entered; otherwise the end is cleared so it cannot disagree with the corrected times. The last period always runs to the check-out (L1).
    /// </summary>
    private static void KeepEndsConsistent(IReadOnlyList<WorkAssignment> periods, DateTimeOffset? checkIn, DateTimeOffset? checkOut)
    {
        for (var i = 0; i < periods.Count; i++)
        {
            var period = periods[i];
            if (period.EndsAt is not { } ends)
            {
                continue;
            }

            var start = period.StartsAt ?? checkIn;
            var last = i == periods.Count - 1;
            var valid = !last
                && (start is null || ends > start)
                && ends <= (periods[i + 1].StartsAt ?? ends)
                && (checkOut is null || ends <= checkOut);
            if (!valid)
            {
                period.EndsAt = null;
            }
        }
    }

    private async Task<AttendanceDayView> ViewAsync(Employee employee, DateOnly day, CancellationToken cancellationToken)
    {
        var record = await attendances.GetAsync(employee.Id, day, cancellationToken);
        var periods = await assignments.ListOfEmployeeAsync(employee.Id, day, day, cancellationToken);
        var hours = WorkHoursCalculator.Calculate(
            record?.CheckInAt, record?.CheckOutAt, periods, AttendanceSettingsVersion.EffectiveOn(await settings.ListAsync(cancellationToken), day));
        var names = periods.ToDictionary(p => p.JobTypeId, p => p.JobType.Name);
        return new AttendanceDayView(
            employee.Id, employee.FullName, WorkRules.TypeOn(employee, day), day, record?.CheckInAt, record?.CheckOutAt,
            record?.CheckInMethod, record?.CheckOutMethod, record?.IsComplete == true,
            periods.OrderBy(p => p.StartsAt is null ? 0 : 1).ThenBy(p => p.StartsAt)
                .Select(p => new DayPeriodView(p.Id, p.JobTypeId, p.JobType.Name, p.RaftId, p.Raft?.Code, p.StartsAt, p.ZoneId)).ToList(),
            hours.Jobs.Select(j => new DayJobHoursView(j.JobTypeId, names[j.JobTypeId], j.Hours)).ToList(),
            record is null ? null : RowVersionGuard.Encode(record.RowVersion));
    }

    private static object Snapshot(Attendance? record, IEnumerable<WorkAssignment> periods) => new
    {
        record?.CheckInAt, record?.CheckOutAt, record?.CheckInMethod, record?.CheckOutMethod,
        Periods = periods.Select(p => new { p.JobTypeId, p.RaftId, p.StartsAt }).ToList(),
    };

    private static object Snapshot(Attendance record, IEnumerable<DayPeriodInput> periods) => new
    {
        record.CheckInAt, record.CheckOutAt, record.CheckInMethod, record.CheckOutMethod,
        Periods = periods.Select(p => new { p.JobTypeId, p.RaftId, p.StartsAt }).ToList(),
    };
}
