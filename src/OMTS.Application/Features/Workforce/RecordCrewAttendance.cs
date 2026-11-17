using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Farming;
using OMTS.Application.Features.Labor.Work;
using OMTS.Domain.Common;
using OMTS.Domain.Entities.Labor;

namespace OMTS.Application.Features.Workforce;

/// <summary>An employee of a record on the day of the record: the day the work assignment and the attendance are looked at.</summary>
public sealed record CrewDay(Guid EmployeeId, DateOnly Day);

/// <summary>The record a completion is about, for the audit row of an override.</summary>
public sealed record RecordRef(string Type, Guid Id, string? Code, Guid? ZoneId);

/// <summary>The Cooperative Owner's answer to MSG-LABOR-37: complete anyway (<c>ownerApproved</c>), with a reason of 1 to 1000 characters (<c>approvalReason</c>).</summary>
public sealed record CompletionApproval(bool OwnerApproved, string? Reason);

/// <summary>An employee still to be checked out, as the 409 of MSG-LABOR-37 lists them.</summary>
public sealed record OpenAttendanceEmployee(Guid Id, string? Code, string Name, bool CheckedIn);

public static class CrewAttendanceStatus
{
    /// <summary>No work assignment that day (a back-dated record, or one skipped when the record was saved) and no attendance.</summary>
    public const string NotAssigned = "NotAssigned";
    public const string NotCheckedIn = "NotCheckedIn";
    public const string Working = "Working";
    public const string Done = "Done";
}

/// <summary>
/// What the crew of a nursery, seeding or harvest record did on the day of the record (decision of 17/11/2026), and the one rule that depends on it: the record cannot be completed while an employee who has
/// a work assignment (zone or raft) that day is not checked out. An employee with no assignment that day never blocks. Written once for the three completions.
/// </summary>
public sealed class RecordCrewAttendance(
    IWorkAssignmentRepository assignments,
    IAttendanceRepository attendances,
    IWorkEmployeeReader employeeReader,
    ZoneAccessGuard zones,
    AuditLogService audit)
{
    public const string ReasonCode = "ATTENDANCE_OPEN";
    public const string OverrideAudit = "Record.CompletedWithOpenAttendance";

    private sealed record State(bool Assigned, Attendance? Record);

    /// <summary>The crew of one record as its view shows it: the work time and the attendance status of each member on the day of the record.</summary>
    public async Task<IReadOnlyList<CrewMemberView>> ViewAsync(IEnumerable<IRecordEmployee> crew, DateOnly day, CancellationToken cancellationToken)
    {
        var members = crew.ToList();
        var states = await ReadAsync(members.Select(m => new CrewDay(m.EmployeeId, day)), cancellationToken);
        return Build(members, day, states);
    }

    /// <summary>The same for many records at once (the lots of a list): one read per day instead of one per record.</summary>
    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<CrewMemberView>>> ViewsAsync(
        IEnumerable<(Guid RecordId, IEnumerable<IRecordEmployee> Crew, DateOnly Day)> records, CancellationToken cancellationToken)
    {
        var list = records.Select(r => (r.RecordId, Crew: r.Crew.ToList(), r.Day)).ToList();
        var states = await ReadAsync(list.SelectMany(r => r.Crew.Select(m => new CrewDay(m.EmployeeId, r.Day))), cancellationToken);
        return list.ToDictionary(r => r.RecordId, r => Build(r.Crew, r.Day, states));
    }

    /// <summary>
    /// Null when the record may be completed. Otherwise 409 MSG-LABOR-37 with <c>reasonCode</c> <c>ATTENDANCE_OPEN</c>, <c>employees</c> and <c>confirmFlag</c> <c>ownerApproved</c>; the Cooperative Owner, and only
    /// the Owner, completes anyway with <paramref name="approval"/> and a reason, which the audit log keeps.
    /// </summary>
    public async Task<AppError?> CheckCompletionAsync(IEnumerable<CrewDay> crew, CompletionApproval? approval, RecordRef record, CancellationToken cancellationToken)
    {
        var days = crew.Distinct().ToList();
        if (days.Count == 0)
        {
            return null;
        }

        var states = await ReadAsync(days, cancellationToken);
        var open = days.Where(d => states[d] is { Assigned: true } s && s.Record?.CheckOutAt is null).ToList();
        if (open.Count == 0)
        {
            return null;
        }

        var people = (await employeeReader.GetForWorkAsync(open.Select(d => d.EmployeeId).Distinct().ToList(), cancellationToken)).ToDictionary(e => e.Id);
        var employees = open.Select(d => d.EmployeeId).Distinct()
            .Where(people.ContainsKey)
            .Select(id => new OpenAttendanceEmployee(id, people[id].Code, people[id].FullName, open.Any(d => d.EmployeeId == id && states[d].Record?.CheckInAt is not null)))
            .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

        if (approval is not { OwnerApproved: true })
        {
            return AppError.Conflict(
                WorkMessages.AttendanceOpen(employees.Select(e => e.Name).ToList()),
                new Dictionary<string, object?> { ["reasonCode"] = ReasonCode, ["confirmFlag"] = "ownerApproved", ["employees"] = employees });
        }

        if (!await zones.IsCooperativeOwnerAsync(cancellationToken))
        {
            return AppError.Forbidden;
        }

        var check = new FieldCheck();
        var reason = check.Required(approval.Reason, "ApprovalReason", WorkRules.MaxReason);
        if (!check.IsValid)
        {
            return AppError.InvalidFields(check.Invalid);
        }

        audit.Record(new AuditEntry(OverrideAudit, record.Type)
        {
            RecordId = record.Id, RecordCode = record.Code, ZoneId = record.ZoneId, Reason = reason,
            After = new { NotCheckedOut = employees.Select(e => e.Code is { Length: > 0 } code ? $"{code} {e.Name}" : e.Name).ToList() },
        });
        return null;
    }

    private async Task<Dictionary<CrewDay, State>> ReadAsync(IEnumerable<CrewDay> days, CancellationToken cancellationToken)
    {
        var all = days.Distinct().ToList();
        var states = new Dictionary<CrewDay, State>();
        foreach (var group in all.GroupBy(d => d.Day))
        {
            var ids = group.Select(d => d.EmployeeId).Distinct().ToList();
            var assigned = (await assignments.ListOfEmployeesAsync(ids, group.Key, cancellationToken)).Select(a => a.EmployeeId).ToHashSet();
            var records = (await attendances.ListOfEmployeesAsync(ids, group.Key, cancellationToken)).ToDictionary(a => a.EmployeeId);
            foreach (var d in group)
            {
                states[d] = new State(assigned.Contains(d.EmployeeId), records.GetValueOrDefault(d.EmployeeId));
            }
        }

        return states;
    }

    /// <summary>
    /// <c>work.minutes</c> is the raw time from check-in to check-out, with no break taken off and no rounding (the paid hours are worked out when shown, from the attendance settings); null until the check-out.
    /// </summary>
    private static IReadOnlyList<CrewMemberView> Build(IReadOnlyList<IRecordEmployee> members, DateOnly day, IReadOnlyDictionary<CrewDay, State> states) =>
        RecordCrewService.ViewOf(members).Select(view =>
        {
            var state = states[new CrewDay(view.Id, day)];
            var record = state.Record;
            var work = record is null ? null : new CrewWorkView(
                record.CheckInAt, record.CheckOutAt,
                record.CheckInAt is { } i && record.CheckOutAt is { } o && o > i ? (int)Math.Floor((o - i).TotalMinutes) : null);
            var status = record?.CheckOutAt is not null ? CrewAttendanceStatus.Done
                : record?.CheckInAt is not null ? CrewAttendanceStatus.Working
                : state.Assigned ? CrewAttendanceStatus.NotCheckedIn
                : CrewAttendanceStatus.NotAssigned;
            return view with { Work = work, Attendance = status };
        }).ToList();
}
