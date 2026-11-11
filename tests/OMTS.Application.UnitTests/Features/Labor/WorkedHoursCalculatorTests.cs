using OMTS.Application.Features.Labor.Hours;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;

namespace OMTS.Application.UnitTests.Features.Labor;

/// <summary>BR-LABOR-05 and AC-10 on fixed times: the example of the SRS, the raft example of BR-LABOR-07, the break, the rounding and the incomplete day.</summary>
public sealed class WorkedHoursCalculatorTests
{
    private static readonly Guid Harvesting = Guid.NewGuid();
    private static readonly Guid Hanging = Guid.NewGuid();
    private static readonly Guid RaftB01 = Guid.NewGuid();
    private static readonly Guid RaftB02 = Guid.NewGuid();

    /// <summary>11/11/2026 in Vietnam time, as the SRS examples read their times.</summary>
    private static DateTimeOffset At(int hour, int minute, int day = 11) => new(2026, 11, day, hour, minute, 0, TimeSpan.FromHours(7));

    private static AttendanceSettingsVersion Settings(AttendanceRoundingUnit unit, bool withBreak = true) => new()
    {
        EffectiveFrom = new DateOnly(2026, 1, 1),
        BreakStart = withBreak ? new TimeOnly(11, 30) : null,
        BreakEnd = withBreak ? new TimeOnly(12, 30) : null,
        RoundingUnit = unit,
    };

    private static WorkAssignment Period(Guid jobType, Guid raft, DateTimeOffset? start) => new() { JobTypeId = jobType, RaftId = raft, StartTime = start, WorkDate = new DateOnly(2026, 11, 11) };

    private static IReadOnlyList<JobTypeHours> HoursOf(DateTimeOffset? checkIn, DateTimeOffset? checkOut, AttendanceSettingsVersion? settings, params WorkAssignment[] periods) =>
        WorkedHoursCalculator.HoursOf(WorkedHoursCalculator.PeriodsOf(checkIn, checkOut, periods)!, settings);

    // AC-10: check-in 06:10, job change 09:40, check-out 16:55, break 11:30-12:30, unit 15 minutes.
    [Fact]
    public void Ac10_gives_3h30_of_the_first_job_type_and_6h15_of_the_second()
    {
        var hours = HoursOf(At(6, 10), At(16, 55), Settings(AttendanceRoundingUnit.Minutes15), Period(Harvesting, RaftB01, null), Period(Hanging, RaftB01, At(9, 40)));

        Assert.Equal(2, hours.Count);
        Assert.Equal((Harvesting, 210, 210), (hours[0].JobTypeId, hours[0].WorkedMinutes, hours[0].RoundedMinutes));
        Assert.Equal(3 * 60 + 30, hours[0].RoundedMinutes);
        // 09:40 to 16:55 is 435 minutes, less the hour of break inside it.
        Assert.Equal((Hanging, 375, 375), (hours[1].JobTypeId, hours[1].WorkedMinutes, hours[1].RoundedMinutes));
        Assert.Equal(6 * 60 + 15, hours[1].RoundedMinutes);
    }

    // BR-LABOR-05 / BR-LABOR-07: harvesting on B01 from 06:00 to 08:10 and on B02 from 08:10 to 10:20 is 4 h 20, rounded down to 4 h 15.
    [Fact]
    public void The_raft_example_adds_the_periods_of_one_job_type_over_every_raft_and_rounds_down_once()
    {
        var hours = HoursOf(At(6, 0), At(10, 20), Settings(AttendanceRoundingUnit.Minutes15), Period(Harvesting, RaftB01, null), Period(Harvesting, RaftB02, At(8, 10)));

        var harvesting = Assert.Single(hours);
        Assert.Equal(4 * 60 + 20, harvesting.WorkedMinutes);
        Assert.Equal(4 * 60 + 15, harvesting.RoundedMinutes);
        Assert.Equal([(RaftB01, 130.0), (RaftB02, 130.0)], harvesting.Periods.Select(p => (p.RaftId, p.Worked.TotalMinutes)));
    }

    [Theory]
    [InlineData(AttendanceRoundingUnit.None, 260)]
    [InlineData(AttendanceRoundingUnit.Minutes15, 255)]
    [InlineData(AttendanceRoundingUnit.Minutes30, 240)]
    [InlineData(AttendanceRoundingUnit.Hour1, 240)]
    public void The_total_is_rounded_down_to_the_unit_and_never_up(AttendanceRoundingUnit unit, int expected)
    {
        var hours = HoursOf(At(6, 0), At(10, 20), Settings(unit), Period(Harvesting, RaftB01, null));

        Assert.Equal(expected, hours.Single().RoundedMinutes);
        Assert.Equal(260, hours.Single().WorkedMinutes);
    }

    [Theory]
    [InlineData(0, AttendanceRoundingUnit.Minutes15, 0)]
    [InlineData(14, AttendanceRoundingUnit.Minutes15, 0)]
    [InlineData(15, AttendanceRoundingUnit.Minutes15, 15)]
    [InlineData(59, AttendanceRoundingUnit.Hour1, 0)]
    [InlineData(61, AttendanceRoundingUnit.None, 61)]
    public void Rounding_down_works_on_whole_minutes(int minutes, AttendanceRoundingUnit unit, int expected) =>
        Assert.Equal(expected, WorkedHoursCalculator.RoundDown(minutes, unit));

    [Fact]
    public void With_no_settings_there_is_no_break_and_no_rounding()
    {
        var hours = HoursOf(At(10, 0), At(14, 7), null, Period(Harvesting, RaftB01, null));

        Assert.Equal((247, 247), (hours.Single().WorkedMinutes, hours.Single().RoundedMinutes));
    }

    [Theory]
    [InlineData(6, 0, 10, 0, 240)]    // ends before the break: nothing to take off
    [InlineData(13, 0, 15, 0, 120)]   // starts after the break
    [InlineData(11, 0, 12, 0, 30)]    // ends inside the break: only the half hour before it counts
    [InlineData(12, 0, 13, 0, 30)]    // starts inside the break
    [InlineData(11, 30, 12, 30, 0)]   // the break itself
    [InlineData(11, 40, 12, 20, 0)]   // inside the break
    [InlineData(10, 0, 14, 0, 180)]   // around the break
    public void Only_the_part_of_the_break_inside_the_period_is_taken_off(int fromHour, int fromMinute, int toHour, int toMinute, int expected)
    {
        var worked = WorkedHoursCalculator.WorkedTime(At(fromHour, fromMinute), At(toHour, toMinute), new TimeOnly(11, 30), new TimeOnly(12, 30));

        Assert.Equal(expected, worked.TotalMinutes);
    }

    // The break is a Vietnam time of day: a period given in UTC is read in Vietnam time (04:00Z is 11:00 there).
    [Fact]
    public void The_break_window_is_read_in_vietnam_time_whatever_offset_the_times_come_with()
    {
        var start = new DateTimeOffset(2026, 11, 11, 4, 0, 0, TimeSpan.Zero);

        var worked = WorkedHoursCalculator.WorkedTime(start, start.AddHours(2), new TimeOnly(11, 30), new TimeOnly(12, 30));

        Assert.Equal(60, worked.TotalMinutes);
    }

    [Fact]
    public void A_period_that_crosses_midnight_loses_the_break_of_each_day()
    {
        var worked = WorkedHoursCalculator.WorkedTime(At(10, 0), At(13, 0, day: 12), new TimeOnly(11, 30), new TimeOnly(12, 30));

        Assert.Equal(27 * 60 - 120, worked.TotalMinutes);
    }

    [Fact]
    public void A_break_that_is_not_a_window_takes_nothing_off()
    {
        Assert.Equal(240, WorkedHoursCalculator.WorkedTime(At(8, 0), At(12, 0), new TimeOnly(11, 30), null).TotalMinutes);
        Assert.Equal(240, WorkedHoursCalculator.WorkedTime(At(8, 0), At(12, 0), new TimeOnly(12, 30), new TimeOnly(11, 30)).TotalMinutes);
    }

    // BR-LABOR-06: a day with a time missing, or a check-out that is not after the check-in, counts no hours.
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void A_day_with_a_time_missing_has_no_periods(bool hasCheckIn, bool hasCheckOut) =>
        Assert.Null(WorkedHoursCalculator.PeriodsOf(hasCheckIn ? At(6, 0) : null, hasCheckOut ? At(16, 0) : null, [Period(Harvesting, RaftB01, null)]));

    [Fact]
    public void A_check_out_that_is_not_after_the_check_in_has_no_periods()
    {
        Assert.Null(WorkedHoursCalculator.PeriodsOf(At(8, 0), At(8, 0), [Period(Harvesting, RaftB01, null)]));
        Assert.Null(WorkedHoursCalculator.PeriodsOf(At(9, 0), At(8, 0), [Period(Harvesting, RaftB01, null)]));
    }

    // The periods end where the next one starts, whatever order the rows come in, and the last one at the check-out.
    [Fact]
    public void Periods_are_ordered_by_start_and_each_ends_at_the_next_start()
    {
        var periods = WorkedHoursCalculator.PeriodsOf(
            At(6, 0), At(16, 0), [Period(Hanging, RaftB02, At(12, 0)), Period(Harvesting, RaftB01, null), Period(Harvesting, RaftB02, At(9, 0))])!;

        Assert.Equal(
            [(Harvesting, At(6, 0), At(9, 0)), (Harvesting, At(9, 0), At(12, 0)), (Hanging, At(12, 0), At(16, 0))],
            periods.Select(p => (p.JobTypeId, p.Start, p.End)));
    }

    // LABOR-04.4.E2 refuses a job period that does not start between the check-in and the check-out: it has no place in the day.
    [Fact]
    public void A_job_period_that_does_not_start_inside_the_day_is_left_out()
    {
        var periods = WorkedHoursCalculator.PeriodsOf(
            At(6, 0), At(16, 0), [Period(Harvesting, RaftB01, null), Period(Hanging, RaftB01, At(5, 0)), Period(Hanging, RaftB01, At(6, 0)), Period(Hanging, RaftB01, At(16, 0)), Period(Hanging, RaftB01, At(17, 0))])!;

        var only = Assert.Single(periods);
        Assert.Equal((Harvesting, At(6, 0), At(16, 0)), (only.JobTypeId, only.Start, only.End));
    }

    [Fact]
    public void An_hour_of_one_job_type_in_two_separate_periods_is_summed()
    {
        var hours = HoursOf(At(7, 0), At(15, 0), Settings(AttendanceRoundingUnit.Minutes15, withBreak: false),
            Period(Harvesting, RaftB01, null), Period(Hanging, RaftB01, At(9, 0)), Period(Harvesting, RaftB01, At(11, 0)));

        Assert.Equal([(Harvesting, 360), (Hanging, 120)], hours.Select(h => (h.JobTypeId, h.WorkedMinutes)));
    }
}
