using OMTS.Application.Common;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Labor.Work;

/// <summary>One job period of a day as worked: its start and end, and its minutes after the break (BR-LABOR-05).</summary>
public sealed record WorkedPeriod(Guid JobTypeId, Guid RaftId, DateTimeOffset StartsAt, DateTimeOffset EndsAt, int Minutes);

/// <summary>The hours of one job type on a day: the minutes of its periods on every raft, rounded down to the rounding unit.</summary>
public sealed record JobHours(Guid JobTypeId, int Minutes)
{
    public decimal Hours => Math.Round(Minutes / 60m, 2, MidpointRounding.AwayFromZero);
}

/// <param name="Periods">In time order.</param>
/// <param name="Jobs">One entry per job type, rounded down.</param>
public sealed record DayHours(IReadOnlyList<WorkedPeriod> Periods, IReadOnlyList<JobHours> Jobs)
{
    public static readonly DayHours None = new([], []);
}

/// <summary>
/// BR-LABOR-05: the hours of a job period are the time from its start (the check-in or the job change) to its end (the next job change
/// or the check-out), minus the part of the break that falls inside it. For each employee and day the hours of a job type are the total
/// of its periods on every raft, rounded down to the rounding unit. The break is a daily time window (business time, UTC+7). Hours are
/// calculated when they are shown, not stored. A day with no check-in or no check-out counts no hours (BR-LABOR-06).
/// </summary>
public static class WorkHoursCalculator
{
    /// <param name="periods">The job periods of the day, in any order; the first has no start time.</param>
    /// <param name="settings">The attendance settings in force on the day; null means no break and no rounding.</param>
    public static DayHours Calculate(
        DateTimeOffset? checkIn, DateTimeOffset? checkOut, IEnumerable<WorkAssignment> periods, AttendanceSettingsVersion? settings)
    {
        if (checkIn is not { } start || checkOut is not { } end || end <= start)
        {
            return DayHours.None;
        }

        var ordered = periods.OrderBy(p => p.StartsAt is null ? 0 : 1).ThenBy(p => p.StartsAt).ToList();
        var breakWindow = BreakWindow(start, settings);
        var worked = new List<WorkedPeriod>();
        for (var i = 0; i < ordered.Count; i++)
        {
            var from = i == 0 ? start : Max(start, ordered[i].StartsAt ?? start);
            var to = i + 1 < ordered.Count ? Min(end, ordered[i + 1].StartsAt ?? end) : end;
            if (to <= from)
            {
                continue;
            }

            var net = to - from - Overlap(from, to, breakWindow);
            worked.Add(new WorkedPeriod(ordered[i].JobTypeId, ordered[i].RaftId, from, to, (int)Math.Floor(net.TotalMinutes)));
        }

        var unit = Minutes(settings?.RoundingUnit ?? AttendanceRoundingUnit.None);
        var jobs = worked.GroupBy(p => p.JobTypeId)
            .Select(g => new JobHours(g.Key, RoundDown(g.Sum(p => p.Minutes), unit)))
            .ToList();
        return new DayHours(worked, jobs);
    }

    /// <summary>The unit in minutes; 0 for none.</summary>
    public static int Minutes(AttendanceRoundingUnit unit) => unit switch
    {
        AttendanceRoundingUnit.Minutes15 => 15,
        AttendanceRoundingUnit.Minutes30 => 30,
        AttendanceRoundingUnit.Hour1 => 60,
        _ => 0,
    };

    private static int RoundDown(int minutes, int unit) => unit == 0 ? minutes : minutes / unit * unit;

    private static (DateTimeOffset From, DateTimeOffset To)? BreakWindow(DateTimeOffset checkIn, AttendanceSettingsVersion? settings)
    {
        if (settings is not { BreakStart: { } breakStart, BreakEnd: { } breakEnd })
        {
            return null;
        }

        var day = BusinessClock.ToBusinessDate(checkIn);
        return (At(day, breakStart), At(day, breakEnd));
    }

    private static DateTimeOffset At(DateOnly day, TimeOnly time) => BusinessClock.FromLocal(day.ToDateTime(time));

    private static TimeSpan Overlap(DateTimeOffset from, DateTimeOffset to, (DateTimeOffset From, DateTimeOffset To)? window)
    {
        if (window is not { } w)
        {
            return TimeSpan.Zero;
        }

        var overlap = Min(to, w.To) - Max(from, w.From);
        return overlap > TimeSpan.Zero ? overlap : TimeSpan.Zero;
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a >= b ? a : b;

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a <= b ? a : b;
}
