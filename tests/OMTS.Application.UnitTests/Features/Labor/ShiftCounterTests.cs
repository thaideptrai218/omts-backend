using OMTS.Application.Features.Labor.Work;
using OMTS.Domain.Entities.Labor;

namespace OMTS.Application.UnitTests.Features.Labor;

/// <summary>A shift is 3 hours of actual work, not a time slot: the shifts of a day are the worked minutes less the break, plus the tolerance, in shifts floored to the half.</summary>
public sealed class ShiftCounterTests
{
    private static readonly DateOnly Day = new(2026, 9, 10);

    private static DateTimeOffset At(string time) => new(Day.ToDateTime(TimeOnly.Parse(time)), TimeSpan.FromHours(7));

    private static AttendanceSettingsVersion Settings(int shiftMinutes = 180, int tolerance = 15) => new()
    {
        EffectiveFrom = new DateOnly(2026, 1, 1),
        BreakStart = new TimeOnly(11, 30),
        BreakEnd = new TimeOnly(12, 30),
        ShiftMinutes = shiftMinutes,
        ShiftToleranceMinutes = tolerance,
    };

    /// <summary>The shifts of a day of one job from the check-in to the check-out, through the same hours the payroll uses.</summary>
    private static decimal ShiftsOf(string checkIn, string checkOut, AttendanceSettingsVersion? settings)
    {
        var assignment = new WorkAssignment { JobTypeId = Guid.NewGuid(), Date = Day };
        var hours = WorkHoursCalculator.Calculate(At(checkIn), At(checkOut), [assignment], settings);
        return ShiftCounter.ShiftsOf(hours.Jobs.Sum(j => j.Minutes), settings);
    }

    [Theory]
    [InlineData("07:00", "18:00", 3.0)]
    [InlineData("05:00", "17:00", 3.5)]
    [InlineData("07:00", "15:50", 2.5)]
    [InlineData("07:10", "17:00", 3.0)]
    public void The_shifts_of_a_day_with_a_break_of_one_hour_and_a_tolerance_of_fifteen_minutes(string checkIn, string checkOut, double shifts) =>
        Assert.Equal((decimal)shifts, ShiftsOf(checkIn, checkOut, Settings()));

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-5, 0)]
    [InlineData(74, 0)]
    [InlineData(75, 0.5)]
    [InlineData(164, 0.5)]
    [InlineData(165, 1.0)]
    [InlineData(254, 1.0)]
    [InlineData(255, 1.5)]
    [InlineData(344, 1.5)]
    [InlineData(345, 2.0)]
    [InlineData(600, 3.0)]
    public void Worked_minutes_become_shifts_in_half_steps_with_the_default_settings(int minutes, double shifts) =>
        Assert.Equal((decimal)shifts, ShiftCounter.ShiftsOf(minutes, null));

    [Fact]
    public void A_shift_length_and_a_tolerance_from_the_settings_replace_the_defaults()
    {
        var settings = Settings(shiftMinutes: 240, tolerance: 0);

        Assert.Equal(1.5m, ShiftCounter.ShiftsOf(360, settings));
        Assert.Equal(1.0m, ShiftCounter.ShiftsOf(359, settings));
    }

    [Fact]
    public void The_defaults_are_a_shift_of_three_hours_with_fifteen_minutes_of_tolerance()
    {
        var settings = new AttendanceSettingsVersion();

        Assert.Equal((180, 15), (settings.ShiftMinutes, settings.ShiftToleranceMinutes));
    }
}
