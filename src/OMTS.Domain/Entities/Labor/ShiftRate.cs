using OMTS.Domain.Common;

namespace OMTS.Domain.Entities.Labor;

/// <summary>
/// The pay of one shift of a job type from an effective date, in whole VND, next to the hourly <see cref="PayRate"/> and with the same rules: the shifts of a day are paid at the rate
/// effective on that day, a new rate never rewrites the days before its date, and a date inside a finalized payroll is refused. One row per job type and date.
/// </summary>
public class ShiftRate : AuditableEntity
{
    public Guid JobTypeId { get; set; }
    public JobType JobType { get; set; } = null!;

    public decimal Amount { get; set; }
    public DateOnly EffectiveFrom { get; set; }
    public Guid EnteredByAccountId { get; set; }

    /// <summary>The rate of <paramref name="day"/>: the latest from that day or before; none for a day before every rate (shifts without a rate block finalizing).</summary>
    public static ShiftRate? EffectiveOn(IEnumerable<ShiftRate> rates, DateOnly day) =>
        rates.Where(r => r.EffectiveFrom <= day).MaxBy(r => r.EffectiveFrom);
}
