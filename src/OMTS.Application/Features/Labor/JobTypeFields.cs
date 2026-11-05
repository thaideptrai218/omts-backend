using OMTS.Application.Common.Validation;
using OMTS.Application.Features.Farming;

namespace OMTS.Application.Features.Labor;

/// <summary>LABOR-11.0.E1: the fields of a job type and of a rate; the invalid ones go on the check, so MSG-GEN-01 lists them all.</summary>
internal static class JobTypeFields
{
    /// <summary>The JobTypes.Name column.</summary>
    public const int MaxName = 100;

    /// <summary>
    /// Not an SRS limit: a rate above it can only be a typing error, and the amounts of a payroll must fit decimal(18,0) when
    /// the hours of a whole period are multiplied by it.
    /// </summary>
    public const decimal MaxHourlyRate = 999_999_999m;

    /// <summary>
    /// As a person reads it (BR-LABOR-12): what is stored and compared is this one spelling, so the unique index holds whatever
    /// the keyboard typed. A name of only invisible characters comes out empty, so it counts as missing.
    /// </summary>
    public static string Name(FieldCheck check, string? name) => check.Required(DisplayNames.Normalize(name), "Name", MaxName);

    /// <summary>Whole VND, greater than 0 (BR-LABOR-12); 40000.0 is stored as 40000.</summary>
    public static decimal HourlyRate(FieldCheck check, decimal? rate)
    {
        var whole = rate is { } value && value == decimal.Truncate(value) && value is > 0 and <= MaxHourlyRate;
        check.Check(whole, "Hourly rate");
        return whole ? decimal.Truncate(rate!.Value) : 0;
    }

    public static DateOnly EffectiveDate(FieldCheck check, DateOnly? date)
    {
        check.Check(date is not null, "Effective date");
        return date ?? default;
    }
}
