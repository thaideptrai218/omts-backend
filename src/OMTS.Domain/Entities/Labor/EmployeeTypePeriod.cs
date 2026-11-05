using OMTS.Domain.Common;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;

namespace OMTS.Domain.Entities.Labor;

/// <summary>
/// One employee type with the date from which it applies, so a change during a pay period keeps the days before it and
/// the earlier types stay (BR-LABOR-01). One row per employee and date.
/// </summary>
public class EmployeeTypePeriod : AuditableEntity
{
    public Guid EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;

    public EmployeeType Type { get; set; }
    public DateOnly EffectiveFrom { get; set; }

    /// <summary>The type on <paramref name="day"/>: the latest row from that day or before; a day before every row has the first type.</summary>
    public static EmployeeType? TypeOn(IEnumerable<EmployeeTypePeriod> periods, DateOnly day)
    {
        EmployeeTypePeriod? latest = null;
        EmployeeTypePeriod? first = null;
        foreach (var period in periods)
        {
            if (first is null || period.EffectiveFrom < first.EffectiveFrom)
            {
                first = period;
            }

            if (period.EffectiveFrom <= day && (latest is null || period.EffectiveFrom > latest.EffectiveFrom))
            {
                latest = period;
            }
        }

        return (latest ?? first)?.Type;
    }
}
