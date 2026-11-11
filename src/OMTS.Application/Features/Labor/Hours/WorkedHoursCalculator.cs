using OMTS.Application.Common;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Labor.Hours;

/// <summary>One job period of a complete day: from its start to its end, on a raft, at a job type.</summary>
public sealed record WorkedPeriod(Guid JobTypeId, Guid RaftId, DateTimeOffset Start, DateTimeOffset End);

/// <summary>The time worked in one period after the break, for the labour cost split across rafts (BR-LABOR-07).</summary>
public sealed record WorkedPeriodTime(Guid RaftId, TimeSpan Worked);

/// <summary>
/// The hours of one job type on one day, over every raft. <see cref="WorkedMinutes"/> is the total of its periods after the
/// break; <see cref="RoundedMinutes"/> is that total rounded down to the rounding unit, and is what is paid (BR-LABOR-05).
/// </summary>
public sealed record JobTypeHours(Guid JobTypeId, int WorkedMinutes, int RoundedMinutes, IReadOnlyList<WorkedPeriodTime> Periods);

/// <summary>
/// BR-LABOR-05, pure: the hours of a day from its check-in, its check-out and its job periods, with the break and the rounding
/// unit of the settings in force on that day. Hours are calculated when shown and not stored until the payroll is Finalized, and
/// only a complete day counts (BR-LABOR-06). The break window is a Vietnam local time of day, as every business time is.
/// </summary>
public static class WorkedHoursCalculator
{
    /// <summary>
    /// The job periods of a day: the first starts at the check-in and each later one at its start time, and each ends where the
    /// next starts or at the check-out. Null while the day is incomplete (a time missing, or the check-out not after the check-in).
    /// A start time that is not strictly between the two times has no place in the day (LABOR-04.4.E2 refuses to save it): it is left out.
    /// </summary>
    public static IReadOnlyList<WorkedPeriod>? PeriodsOf(DateTimeOffset? checkIn, DateTimeOffset? checkOut, IEnumerable<WorkAssignment> assignments)
    {
        if (checkIn is not { } start || checkOut is not { } end || end <= start)
        {
            return null;
        }

        var starts = assignments
            .Select(a => (Assignment: a, Start: a.StartTime ?? start))
            .Where(x => x.Assignment.StartTime is null || (x.Start > start && x.Start < end))
            .OrderBy(x => x.Start)
            .ToList();
        var periods = new List<WorkedPeriod>(starts.Count);
        for (var i = 0; i < starts.Count; i++)
        {
            var next = i + 1 < starts.Count ? starts[i + 1].Start : end;
            periods.Add(new WorkedPeriod(starts[i].Assignment.JobTypeId, starts[i].Assignment.RaftId, starts[i].Start, next));
        }

        return periods;
    }

    /// <summary>The hours per job type, in the order of the first period of each; <paramref name="settings"/> null means no break and no rounding.</summary>
    public static IReadOnlyList<JobTypeHours> HoursOf(IEnumerable<WorkedPeriod> periods, AttendanceSettingsVersion? settings)
    {
        var unit = settings?.RoundingUnit ?? AttendanceRoundingUnit.None;
        return periods
            .GroupBy(p => p.JobTypeId)
            .Select(group =>
            {
                var times = group.Select(p => new WorkedPeriodTime(p.RaftId, WorkedTime(p.Start, p.End, settings?.BreakStart, settings?.BreakEnd))).ToList();
                var minutes = (int)Math.Floor(times.Sum(t => t.Worked.TotalMinutes));
                return new JobTypeHours(group.Key, minutes, RoundDown(minutes, unit), times);
            })
            .ToList();
    }

    /// <summary>The time from <paramref name="start"/> to <paramref name="end"/> minus the part of the break inside it, on each day the period touches.</summary>
    public static TimeSpan WorkedTime(DateTimeOffset start, DateTimeOffset end, TimeOnly? breakStart, TimeOnly? breakEnd)
    {
        var total = end - start;
        if (total <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        if (breakStart is not { } breakFrom || breakEnd is not { } breakTo || breakTo <= breakFrom)
        {
            return total;
        }

        var (localStart, localEnd) = (start.ToOffset(BusinessClock.UtcOffset).DateTime, end.ToOffset(BusinessClock.UtcOffset).DateTime);
        var inBreak = TimeSpan.Zero;
        for (var day = localStart.Date; day <= localEnd.Date; day = day.AddDays(1))
        {
            var overlapStart = Max(localStart, day + breakFrom.ToTimeSpan());
            var overlapEnd = Min(localEnd, day + breakTo.ToTimeSpan());
            if (overlapEnd > overlapStart)
            {
                inBreak += overlapEnd - overlapStart;
            }
        }

        return total - inBreak;
    }

    /// <summary>Rounded down to the unit: 4 h 20 with 15 minutes is 4 h 15. No unit keeps the minutes.</summary>
    public static int RoundDown(int minutes, AttendanceRoundingUnit unit) =>
        unit == AttendanceRoundingUnit.None ? minutes : minutes / (int)unit * (int)unit;

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
}
