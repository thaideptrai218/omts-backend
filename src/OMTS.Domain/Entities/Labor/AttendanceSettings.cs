using OMTS.Domain.Common;
namespace OMTS.Domain.Entities.Labor;

/// <summary>LABOR-15: immutable dated versions; none implies hours are not calculated yet.</summary>
public class AttendanceSettings : AuditableEntity
{
    public DateOnly EffectiveFrom { get; set; }
    public TimeOnly? BreakFrom { get; set; }
    public TimeOnly? BreakTo { get; set; }
    public int RoundingMinutes { get; set; }
}
