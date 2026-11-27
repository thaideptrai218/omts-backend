using OMTS.Domain.Common;
using OMTS.Domain.Enums;

namespace OMTS.Domain.Entities.Labor;

/// <summary>
/// The break, the rounding of worked hours and the length of a shift from a date (LABOR-15, BR-LABOR-05): the hours and shifts of a day use the settings in
/// force on that day, and the earlier settings stay. Before the first row there is no break and no rounding, and a shift has its default length and tolerance.
/// </summary>
public class AttendanceSettingsVersion : AuditableEntity
{
    public const int DefaultShiftMinutes = 180;
    public const int DefaultShiftToleranceMinutes = 15;
    public const int MinShiftMinutes = 30;
    public const int MaxShiftMinutes = 720;

    public DateOnly EffectiveFrom { get; set; }

    /// <summary>Both times or neither: no break.</summary>
    public TimeOnly? BreakStart { get; set; }
    public TimeOnly? BreakEnd { get; set; }

    public AttendanceRoundingUnit RoundingUnit { get; set; }

    /// <summary>The minutes of actual work in one shift (a shift is not a time slot of the day).</summary>
    public int ShiftMinutes { get; set; } = DefaultShiftMinutes;

    /// <summary>Minutes added to the worked minutes of a job before they are divided into shifts, so a few minutes short of a half shift still count it.</summary>
    public int ShiftToleranceMinutes { get; set; } = DefaultShiftToleranceMinutes;

    public Guid EnteredByAccountId { get; set; }

    /// <summary>The settings of <paramref name="day"/>: the latest from that day or before; null means no break and no rounding.</summary>
    public static AttendanceSettingsVersion? EffectiveOn(IEnumerable<AttendanceSettingsVersion> versions, DateOnly day) =>
        versions.Where(v => v.EffectiveFrom <= day).MaxBy(v => v.EffectiveFrom);
}
