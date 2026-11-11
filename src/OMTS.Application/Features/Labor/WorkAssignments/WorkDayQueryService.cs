using OMTS.Application.Common;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Labor.Hours;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Labor.WorkAssignments;

/// <summary>
/// LABOR-02.0 step 1 (SCR-LABOR-02): for a date, the rafts In Use in the caller's zones with their crews, each member with the job
/// type now, the check-in and the check-out, and the Staff employees of the caller's zones with theirs. A member is on the crew of
/// the raft of their latest job period (BR-LABOR-03). The hours of a day with both times are calculated here and not stored (BR-LABOR-05).
/// </summary>
public sealed class WorkDayQueryService(
    IWorkAssignmentRepository assignments,
    IAttendanceDayReader attendance,
    IAttendanceSettingsRepository settings,
    ZoneAccessGuard zoneAccess,
    TimeProvider timeProvider)
{
    /// <summary>More Staff employees than this in one set of zones is not a staff list a person reads.</summary>
    private const int MaxStaff = 2000;

    public async Task<Result<WorkDayView>> GetAsync(DateOnly date, CancellationToken cancellationToken)
    {
        var zones = await zoneAccess.PermittedZonesAsync(PermissionFunctions.WorkAssignments, PermissionAction.View, cancellationToken);
        if (zones.IsEmpty)
        {
            return AppError.Forbidden;
        }

        var periods = await assignments.ListOnAsync(date, zones, cancellationToken);
        var staff = (await assignments.ListEmployedAsync(zones, null, null, null, MaxStaff, cancellationToken))
            .Where(e => EmployeeTypePeriod.TypeOn(e.TypePeriods, date) == EmployeeType.Staff)
            .ToList();
        var days = await attendance.ReadAsync(date, periods.Select(p => p.EmployeeId).Concat(staff.Select(s => s.Id)).Distinct().ToList(), cancellationToken);
        var rules = AttendanceSettingsVersion.EffectiveOn(await settings.ListAsync(cancellationToken), date);
        var today = timeProvider.Today();

        var members = periods.GroupBy(p => p.EmployeeId)
            .Select(g => Member(g.ToList(), zones, days.GetValueOrDefault(g.Key), date >= today, rules))
            .Where(m => m.HasValue)
            .Select(m => m!.Value)
            .ToList();
        var rafts = (await assignments.ListRaftsInUseAsync(zones, cancellationToken)).ToList();
        // A raft discontinued after work was assigned on it still shows its crew.
        rafts.AddRange(members.Select(m => m.Raft).DistinctBy(r => r.Id).Where(r => rafts.All(x => x.Id != r.Id)).ToList());

        var view = rafts.Select(r =>
        {
            var crew = members.Where(m => m.Raft.Id == r.Id).Select(m => m.View).OrderBy(m => m.FullName).ThenBy(m => m.EmployeeId).ToList();
            var canChangeJob = date == today && members.Any(m => m.Raft.Id == r.Id && days.GetValueOrDefault(m.View.EmployeeId) is { IsAtWork: true });
            return new WorkDayRaft(r.Id, r.Code, r.ZoneId, r.Zone.Code, canChangeJob, crew);
        }).ToList();
        var staffView = staff.Select(s => days.GetValueOrDefault(s.Id) is { } d
            ? new WorkDayStaff(s.Id, s.FullName, d.CheckInAt, d.CheckOutAt)
            : new WorkDayStaff(s.Id, s.FullName, null, null)).ToList();
        return new WorkDayView(date, date >= today, view, staffView);
    }

    /// <summary>
    /// The member as the caller sees them, from ALL job periods of the employee on the day (the repository returns the periods on
    /// rafts outside the caller's zones too): a period ends where the next one starts, wherever that raft is, and the member is on the
    /// crew of the raft of the latest period. When that raft is outside the caller's zones the member is not on their screen
    /// (null); otherwise only the periods on rafts of the caller's zones are listed and counted in the hours.
    /// </summary>
    private static (Raft Raft, WorkDayCrewMember View)? Member(
        IReadOnlyList<WorkAssignment> all, PermittedZones zones, DayAttendance? day, bool notPast, AttendanceSettingsVersion? rules)
    {
        var own = all.OrderBy(p => p.StartTime ?? DateTimeOffset.MinValue).ToList();
        var current = own[^1];
        if (!zones.Allows(current.Raft.ZoneId))
        {
            return null;
        }

        var visible = own.Where(p => zones.Allows(p.Raft.ZoneId)).ToList();
        var first = own.FirstOrDefault(p => p.IsFirstPeriod);
        var addressed = first is not null && zones.Allows(first.Raft.ZoneId) ? first : current;
        IReadOnlyList<WorkDayHours>? hours = null;
        if (day is { IsComplete: true } && WorkedHoursCalculator.PeriodsOf(day.CheckInAt, day.CheckOutAt, own) is { } worked)
        {
            var visibleRafts = visible.Select(p => p.RaftId).ToHashSet();
            var names = own.Select(p => p.JobType).DistinctBy(j => j.Id).ToDictionary(j => j.Id, j => j.Name);
            hours = WorkedHoursCalculator.HoursOf(worked.Where(p => visibleRafts.Contains(p.RaftId)), rules)
                .Select(h => new WorkDayHours(h.JobTypeId, names[h.JobTypeId], h.RoundedMinutes)).ToList();
        }

        var view = new WorkDayCrewMember(
            current.EmployeeId, current.Employee.FullName, addressed.Id, current.JobTypeId, current.JobType.Name, day?.CheckInAt, day?.CheckOutAt,
            notPast && first is not null && own.Count == 1 && day?.IsCheckedIn != true, RowVersionGuard.Encode(addressed.RowVersion),
            visible.Select(p => new WorkDayPeriod(p.Id, p.RaftId, p.Raft.Code, p.JobTypeId, p.JobType.Name, p.StartTime)).ToList(), hours);
        return (current.Raft, view);
    }
}
