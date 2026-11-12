using OMTS.Application.Common;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Farming;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Labor.Work;

/// <summary>
/// LABOR-04.4: the Cooperative Owner enters a missing or corrected check-in or check-out of any employee and day, today or earlier, and
/// changes, adds or removes the job periods of the day, with a reason; the times and the periods then have the method Correction and
/// the hours are worked out again when shown (BR-LABOR-05). The audit log keeps the values before and after with the reason. Nothing
/// can be corrected in a pay period whose payroll is Awaiting finalization or Finalized (BR-LABOR-13, MSG-LABOR-04). The client asks
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
    /// <summary>The attendance record and the job periods of an employee on a day, as the correction form shows them. Cooperative Owner only.</summary>
    public async Task<Result<AttendanceDayView>> GetDayAsync(Guid employeeId, DateOnly day, CancellationToken cancellationToken)
    {
        if (!await zones.IsCooperativeOwnerAsync(cancellationToken))
        {
            return AppError.Forbidden;
        }

        var employee = (await employeeReader.GetForWorkAsync([employeeId], cancellationToken)).FirstOrDefault();
        return employee is null ? AppError.NotFound : await ViewAsync(employee, day, cancellationToken);
    }

    public async Task<Result<AttendanceCorrected>> CorrectAsync(Guid employeeId, DateOnly day, CorrectAttendanceRequest request, CancellationToken cancellationToken)
    {
        if (!await zones.IsCooperativeOwnerAsync(cancellationToken))
        {
            return AppError.Forbidden;
        }

        var employee = (await employeeReader.GetForWorkAsync([employeeId], cancellationToken)).FirstOrDefault();
        if (employee is null)
        {
            return AppError.NotFound;
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
        if (record is not null && await rowVersions.CheckAsync(record, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        var existing = (await assignments.ListOfEmployeesAsync([employee.Id], day, cancellationToken))
            .OrderBy(p => p.StartsAt is null ? 0 : 1).ThenBy(p => p.StartsAt).ToList();
        if (await RefuseChoicesAsync(inputs, existing, cancellationToken) is { } refused)
        {
            return refused;
        }

        var account = currentUser.AccountId ?? throw new InvalidOperationException("Attendance is corrected by a logged-in user.");
        var before = Snapshot(record, existing);
        record = ApplyTimes(employee, day, record, request, reason, account);
        ApplyPeriods(employee, day, existing, inputs, reason, account);
        audit.Record(new AuditEntry("Attendance.Corrected", nameof(Attendance))
        {
            RecordId = record.Id, RecordCode = employee.Code, Before = before, After = Snapshot(record, inputs), Reason = reason,
        });
        await unitOfWork.SaveChangesAsync(cancellationToken);

        var saved = UserMessages.Saved("Attendance");
        return new AttendanceCorrected(await ViewAsync(employee, day, cancellationToken), saved.Text, saved.Code);
    }

    /// <summary>The checks of LABOR-04.4.E2: the times are on the day and in order, and each period of a seasonal worker has a job type and a raft and starts between them.</summary>
    private static void Validate(FieldCheck check, Employee employee, DateOnly day, CorrectAttendanceRequest request, IReadOnlyList<DayPeriodInput> inputs)
    {
        check.Check(request.CheckInAt is null || BusinessClock.ToBusinessDate(request.CheckInAt.Value) == day, "Check-in");
        check.Check(request.CheckOutAt is null || BusinessClock.ToBusinessDate(request.CheckOutAt.Value) == day, "Check-out");
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
        check.Check(inputs.All(p => p.RaftId is { } r && r != Guid.Empty), "Raft");
        check.Check(inputs.Count(p => p.StartsAt is null) <= 1, "Job periods");
        check.Check(inputs.Count == 0 || inputs[0].StartsAt is null, "Job periods");
        var starts = inputs.Where(p => p.StartsAt is not null).Select(p => p.StartsAt!.Value).ToList();
        check.Check(starts.Distinct().Count() == starts.Count, "Job periods");
        check.Check(
            starts.All(s => request.CheckInAt is { } i && s > i && (request.CheckOutAt is not { } o || s < o)),
            "Job periods");
    }

    /// <summary>A job type or raft that is new on the day must be In Use (GBR-01); one the day already has stays, as an assignment keeps it.</summary>
    private async Task<AppError?> RefuseChoicesAsync(IReadOnlyList<DayPeriodInput> inputs, IReadOnlyList<WorkAssignment> existing, CancellationToken cancellationToken)
    {
        var jobTypeIds = inputs.Select(p => p.JobTypeId!.Value).Distinct().ToList();
        var jobTypes = (await assignments.GetJobTypesAsync(jobTypeIds, cancellationToken)).ToDictionary(j => j.Id);
        foreach (var id in jobTypeIds)
        {
            if (!jobTypes.TryGetValue(id, out var jobType))
            {
                return AppError.Invalid("Job type");
            }

            if (jobType.StatusId != StatusIds.Usage.InUse && existing.All(p => p.JobTypeId != id))
            {
                return AppError.NotUsableChoice($"Job type {jobType.Name}", statusCatalog.DisplayNameOf(jobType.StatusId), "Job type");
            }
        }

        var raftIds = inputs.Select(p => p.RaftId!.Value).Distinct().ToList();
        var found = (await rafts.GetByIdsAsync(raftIds, cancellationToken)).ToDictionary(r => r.Id);
        foreach (var id in raftIds)
        {
            if (!found.TryGetValue(id, out var raft))
            {
                return AppError.Invalid("Raft");
            }

            if (raft.StatusId != StatusIds.Usage.InUse && existing.All(p => p.RaftId != id))
            {
                return AppError.NotUsableChoice($"Raft {raft.Code}", statusCatalog.DisplayNameOf(raft.StatusId), "Raft");
            }
        }

        return null;
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
    private void ApplyPeriods(Employee employee, DateOnly day, IReadOnlyList<WorkAssignment> existing, IReadOnlyList<DayPeriodInput> inputs, string reason, Guid account)
    {
        for (var i = 0; i < inputs.Count; i++)
        {
            var input = inputs[i];
            if (i >= existing.Count)
            {
                assignments.Add(new WorkAssignment
                {
                    EmployeeId = employee.Id, Date = day, RaftId = input.RaftId!.Value, JobTypeId = input.JobTypeId!.Value, StartsAt = input.StartsAt,
                    AssignedByAccountId = account, Method = AttendanceMethod.Correction, CorrectionReason = reason,
                });
                continue;
            }

            var period = existing[i];
            if (period.RaftId == input.RaftId && period.JobTypeId == input.JobTypeId && period.StartsAt == input.StartsAt)
            {
                continue;
            }

            (period.RaftId, period.JobTypeId, period.StartsAt, period.Method, period.CorrectionReason) =
                (input.RaftId!.Value, input.JobTypeId!.Value, input.StartsAt, AttendanceMethod.Correction, reason);
        }

        foreach (var surplus in existing.Skip(inputs.Count))
        {
            assignments.Remove(surplus);
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
                .Select(p => new DayPeriodView(p.Id, p.JobTypeId, p.JobType.Name, p.RaftId, p.Raft.Code, p.StartsAt)).ToList(),
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
