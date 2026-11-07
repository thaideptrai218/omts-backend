using OMTS.Domain.Common;

namespace OMTS.Domain.Entities.Labor;

/// <summary>
/// The hourly rate of a job type from an effective date, in whole VND (LABOR-11, BR-LABOR-12). The hours of a day are paid at
/// the rate effective on that day, so a new rate never rewrites the days before its date. One row per job type and date.
/// </summary>
public class PayRate : AuditableEntity
{
    public Guid JobTypeId { get; set; }
    public JobType JobType { get; set; } = null!;

    public decimal HourlyRate { get; set; }
    public DateOnly EffectiveFrom { get; set; }
    public Guid EnteredByAccountId { get; set; }

    /// <summary>The rate of <paramref name="day"/>: the latest from that day or before; none for a day before every rate (BR-LABOR-10, hours without a rate).</summary>
    public static PayRate? EffectiveOn(IEnumerable<PayRate> rates, DateOnly day) =>
        rates.Where(r => r.EffectiveFrom <= day).MaxBy(r => r.EffectiveFrom);
}
