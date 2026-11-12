using OMTS.Application.Features.Labor.Work;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;

namespace OMTS.Application.UnitTests.Features.Labor;

/// <summary>BR-LABOR-05 and the acceptance example AC-10: hours of a job type are the time of its periods minus the break, rounded down.</summary>
public sealed class WorkHoursCalculatorTests
{
    private static readonly DateOnly Day = new(2026, 3, 2);
    private static readonly Guid Harvest = Guid.NewGuid();
    private static readonly Guid Hanging = Guid.NewGuid();
    private static readonly Guid RaftB01 = Guid.NewGuid();
    private static readonly Guid RaftB02 = Guid.NewGuid();

    /// <summary>A time of the business day (UTC+7).</summary>
    private static DateTimeOffset At(string time) => new(Day.ToDateTime(TimeOnly.Parse(time)), TimeSpan.FromHours(7));

    private static WorkAssignment Period(Guid jobType, Guid raft, string? startsAt) =>
        new() { JobTypeId = jobType, RaftId = raft, StartsAt = startsAt is null ? null : At(startsAt) };

    private static AttendanceSettingsVersion Settings(string? breakStart, string? breakEnd, AttendanceRoundingUnit unit) => new()
    {
        EffectiveFrom = new DateOnly(2020, 1, 1),
        BreakStart = breakStart is null ? null : TimeOnly.Parse(breakStart),
        BreakEnd = breakEnd is null ? null : TimeOnly.Parse(breakEnd),
        RoundingUnit = unit,
    };

    private static int MinutesOf(DayHours hours, Guid jobType) => hours.Jobs.Single(j => j.JobTypeId == jobType).Minutes;

    [Fact]
    public void The_srs_example_gives_3h30_of_the_first_job_and_6h15_of_the_second()
    {
        var hours = WorkHoursCalculator.Calculate(
            At("06:10"), At("16:55"), [Period(Hanging, RaftB01, null), Period(Harvest, RaftB01, "09:40")], Settings("11:30", "12:30", AttendanceRoundingUnit.Minutes15));

        Assert.Equal(210, MinutesOf(hours, Hanging));
        Assert.Equal(375, MinutesOf(hours, Harvest));
        Assert.Equal(6.25m, hours.Jobs.Single(j => j.JobTypeId == Harvest).Hours);
    }

    [Fact]
    public void The_hours_of_a_job_on_two_rafts_are_added_and_then_rounded_down()
    {
        // 06:00 to 08:10 on B01 and 08:10 to 10:20 on B02 is 4 h 20 of harvesting, rounded down to 4 h 15.
        var hours = WorkHoursCalculator.Calculate(
            At("06:00"), At("10:20"), [Period(Harvest, RaftB01, null), Period(Harvest, RaftB02, "08:10")], Settings("11:30", "12:30", AttendanceRoundingUnit.Minutes15));

        Assert.Equal(255, MinutesOf(hours, Harvest));
        Assert.Equal([130, 130], hours.Periods.Select(p => p.Minutes));
        Assert.Equal([RaftB01, RaftB02], hours.Periods.Select(p => p.RaftId));
    }

    [Theory]
    [InlineData(AttendanceRoundingUnit.None, 260)]
    [InlineData(AttendanceRoundingUnit.Minutes15, 255)]
    [InlineData(AttendanceRoundingUnit.Minutes30, 240)]
    [InlineData(AttendanceRoundingUnit.Hour1, 240)]
    public void Each_rounding_unit_rounds_down(AttendanceRoundingUnit unit, int expected)
    {
        var hours = WorkHoursCalculator.Calculate(At("06:00"), At("10:20"), [Period(Harvest, RaftB01, null)], Settings(null, null, unit));

        Assert.Equal(expected, MinutesOf(hours, Harvest));
    }

    [Fact]
    public void When_OMTS_is_set_up_there_is_no_break_and_no_rounding()
    {
        var hours = WorkHoursCalculator.Calculate(At("06:10"), At("16:55"), [Period(Harvest, RaftB01, null)], settings: null);

        Assert.Equal(645, MinutesOf(hours, Harvest));
    }

    [Theory]
    [InlineData("06:00", "11:00", 300)]
    [InlineData("12:30", "16:00", 210)]
    [InlineData("11:00", "13:00", 60)]
    [InlineData("11:45", "12:15", 0)]
    public void Only_the_part_of_the_break_inside_the_period_is_taken_off(string checkIn, string checkOut, int expected)
    {
        var hours = WorkHoursCalculator.Calculate(At(checkIn), At(checkOut), [Period(Harvest, RaftB01, null)], Settings("11:30", "12:30", AttendanceRoundingUnit.None));

        Assert.Equal(expected, hours.Jobs.Single().Minutes);
    }

    [Fact]
    public void A_break_that_splits_a_period_is_taken_off_the_period_it_falls_in()
    {
        // The job changes at 12:00, in the middle of the break 11:30 to 12:30: 30 minutes of it belong to each period.
        var hours = WorkHoursCalculator.Calculate(
            At("10:00"), At("14:00"), [Period(Hanging, RaftB01, null), Period(Harvest, RaftB01, "12:00")], Settings("11:30", "12:30", AttendanceRoundingUnit.None));

        Assert.Equal(90, MinutesOf(hours, Hanging));
        Assert.Equal(90, MinutesOf(hours, Harvest));
    }

    [Fact]
    public void A_day_without_a_check_in_or_a_check_out_counts_no_hours()
    {
        var periods = new[] { Period(Harvest, RaftB01, null) };
        var settings = Settings(null, null, AttendanceRoundingUnit.None);

        Assert.Empty(WorkHoursCalculator.Calculate(null, At("16:00"), periods, settings).Jobs);
        Assert.Empty(WorkHoursCalculator.Calculate(At("06:00"), null, periods, settings).Jobs);
        Assert.Empty(WorkHoursCalculator.Calculate(At("16:00"), At("06:00"), periods, settings).Jobs);
        Assert.Empty(WorkHoursCalculator.Calculate(At("06:00"), At("06:00"), periods, settings).Jobs);
    }

    [Fact]
    public void A_complete_day_without_job_periods_has_no_job_hours()
    {
        Assert.Empty(WorkHoursCalculator.Calculate(At("06:00"), At("16:00"), [], Settings(null, null, AttendanceRoundingUnit.None)).Periods);
    }

    [Fact]
    public void A_period_that_starts_outside_the_day_is_clamped_to_the_check_in_and_check_out()
    {
        var hours = WorkHoursCalculator.Calculate(
            At("08:00"), At("10:00"), [Period(Hanging, RaftB01, null), Period(Harvest, RaftB01, "07:00"), Period(Hanging, RaftB02, "11:00")], settings: null);

        // The second period "starts" before the check-in and the third after the check-out: only the time inside counts.
        Assert.Equal(120, MinutesOf(hours, Harvest));
        Assert.DoesNotContain(hours.Jobs, j => j.JobTypeId == Hanging);
    }

    [Fact]
    public void The_periods_are_in_time_order_whatever_order_they_are_given_in()
    {
        var hours = WorkHoursCalculator.Calculate(
            At("06:00"), At("12:00"), [Period(Harvest, RaftB02, "09:00"), Period(Hanging, RaftB01, null)], settings: null);

        Assert.Equal([Hanging, Harvest], hours.Periods.Select(p => p.JobTypeId));
        Assert.Equal([180, 180], hours.Periods.Select(p => p.Minutes));
    }
}
