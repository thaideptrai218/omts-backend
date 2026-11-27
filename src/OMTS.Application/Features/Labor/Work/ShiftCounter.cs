using OMTS.Domain.Entities.Labor;

namespace OMTS.Application.Features.Labor.Work;

/// <summary>
/// The shifts of a job on a day. A shift is a set length of actual work (3 hours by default), not a time slot of the day: it is counted from the minutes the job was worked,
/// after the break and the rounding of <see cref="WorkHoursCalculator"/>. The shifts are <c>(worked minutes + tolerance) / shift minutes</c>, floored to the half shift. Each job
/// type of a day is counted on its own minutes, with its own tolerance, so a day split between two job types can give less than the same time in one job.
/// With a break of 1 hour, a tolerance of 15 minutes and a shift of 180 minutes: 07:00-18:00 is 3 shifts, 05:00-17:00 is 3.5, 07:00-15:50 is 2.5 and 07:10-17:00 is 3.
/// </summary>
public static class ShiftCounter
{
    /// <summary>The smallest part of a shift that is counted.</summary>
    public const decimal Step = 0.5m;

    /// <param name="workedMinutes">The paid minutes of one job type on the day.</param>
    /// <param name="settings">The attendance settings in force on the day; null means the default shift length and tolerance.</param>
    public static decimal ShiftsOf(int workedMinutes, AttendanceSettingsVersion? settings)
    {
        if (workedMinutes <= 0)
        {
            return 0;
        }

        var shiftMinutes = settings?.ShiftMinutes ?? AttendanceSettingsVersion.DefaultShiftMinutes;
        var tolerance = settings?.ShiftToleranceMinutes ?? AttendanceSettingsVersion.DefaultShiftToleranceMinutes;
        return Math.Floor((workedMinutes + tolerance) / (decimal)shiftMinutes / Step) * Step;
    }
}
