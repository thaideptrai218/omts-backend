using OMTS.Domain.Common;
using OMTS.Domain.Enums;

namespace OMTS.Domain.Entities.Labor;

/// <summary>
/// The break and the rounding of worked hours from a date (LABOR-15, BR-LABOR-05): the hours of a day use the settings in
/// force on that day, and the earlier settings stay. Before the first row there is no break and no rounding.
/// </summary>
public class AttendanceSettingsVersion : AuditableEntity
{
    public AttendanceSettingsVersion() { }
    public AttendanceSettingsVersion(Guid id) => Id = id;
    public DateOnly EffectiveFrom { get; set; }

    /// <summary>Both times or neither: no break.</summary>
    public TimeOnly? BreakStart { get; set; }
    public TimeOnly? BreakEnd { get; set; }

    public AttendanceRoundingUnit RoundingUnit { get; set; }
    public Guid EnteredByAccountId { get; set; }

    /// <summary>The settings of <paramref name="day"/>: the latest from that day or before; null means no break and no rounding.</summary>
    public static AttendanceSettingsVersion? EffectiveOn(IEnumerable<AttendanceSettingsVersion> versions, DateOnly day) =>
        versions.Where(v => v.EffectiveFrom <= day).MaxBy(v => v.EffectiveFrom);
}
