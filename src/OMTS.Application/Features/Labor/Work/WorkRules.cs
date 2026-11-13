using OMTS.Application.Common;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Labor.Work;

/// <summary>Rules and read models the work assignment, attendance and request services share (BR-LABOR-01, BR-LABOR-03, BR-LABOR-06).</summary>
internal static class WorkRules
{
    /// <summary>The longest reason or note (the length of the Reason columns).</summary>
    public const int MaxReason = 1000;

    /// <summary>The longest reason an employee writes (SCR-LABOR-07).</summary>
    public const int MaxRequestReason = 500;

    /// <summary>
    /// Employed on <paramref name="day"/>: the status is Employed and the last working day, when there is one, is not before the day (an
    /// employee ended today is Left from tomorrow, BR-LABOR-01).
    /// </summary>
    public static bool IsEmployedOn(Employee employee, DateOnly day) =>
        employee.StatusId == StatusIds.Employee.Working && (employee.LastWorkingDay is not { } last || last >= day);

    /// <summary>The employee type in force on the day (BR-LABOR-01).</summary>
    public static EmployeeType TypeOn(Employee employee, DateOnly day) =>
        EmployeeTypePeriod.TypeOn(employee.TypePeriods, day) ?? EmployeeType.SeasonalWorker;

    /// <summary>The time now, to the second: what a check-in or check-out records.</summary>
    public static DateTimeOffset Now(TimeProvider timeProvider)
    {
        var now = timeProvider.GetUtcNow();
        return new DateTimeOffset(now.Ticks - now.Ticks % TimeSpan.TicksPerSecond, now.Offset);
    }

    private static readonly string[] TimeFormats = ["HH:mm", "HH:mm:ss"];

    /// <summary>A time of the day sent as <c>HH:mm</c> or <c>HH:mm:ss</c>; null when it is empty or not one.</summary>
    public static TimeOnly? ParseTime(string? text) =>
        TimeOnly.TryParseExact(text?.Trim(), TimeFormats, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var time) ? time : null;

    /// <summary>A time of a business day (UTC+7) as an instant.</summary>
    public static DateTimeOffset At(DateOnly day, TimeOnly time) => BusinessClock.FromLocal(day.ToDateTime(time));

    /// <summary>
    /// One row per employee of the job periods given (all of one date): the id is the first period, the raft and job are those of the
    /// latest, with the check-in and check-out of the attendance record.
    /// </summary>
    public static IReadOnlyList<AssignmentView> Rows(
        IEnumerable<WorkAssignment> periods, IReadOnlyDictionary<Guid, Attendance> attendance, IReadOnlySet<Guid> onLeave) =>
        periods.GroupBy(p => p.EmployeeId)
            .Select(g =>
            {
                var ordered = g.OrderBy(p => p.StartsAt is null ? 0 : 1).ThenBy(p => p.StartsAt).ToList();
                var first = ordered[0];
                var current = ordered[^1];
                attendance.TryGetValue(first.EmployeeId, out var record);
                return new AssignmentView(
                    first.Id, first.EmployeeId, first.Employee.FullName, current.Raft.ZoneId, current.RaftId, current.Raft.Code,
                    current.JobTypeId, current.JobType.Name, first.Date, record?.CheckInAt, record?.CheckOutAt)
                {
                    EmployeeCode = first.Employee.Code,
                    Complete = record?.IsComplete == true,
                    OnLeave = onLeave.Contains(first.EmployeeId),
                };
            })
            .OrderBy(r => r.EmployeeName, StringComparer.CurrentCultureIgnoreCase).ThenBy(r => r.EmployeeId)
            .ToList();
}
