namespace OMTS.Application.Features.Farming.Licenses;

/// <summary>Optional zone license types (not in the SRS, kept as an optional field) with their display names.</summary>
public static class LicenseTypes
{
    public const string SeaAreaAllocation = "SEA_AREA_ALLOCATION";
    public const string AquacultureLicense = "AQUACULTURE_LICENSE";
    public const string FoodSafetyCommitment = "FOOD_SAFETY_COMMITMENT";
    public const string Other = "OTHER";

    public static readonly IReadOnlyDictionary<string, string> Names = new Dictionary<string, string>
    {
        [SeaAreaAllocation] = "Sea area allocation decision",
        [AquacultureLicense] = "Marine aquaculture license",
        [FoodSafetyCommitment] = "Food safety commitment",
        [Other] = "Other document",
    };

    /// <summary>The display name of a type, or null for a license without one.</summary>
    public static string? NameOf(string? type) => type is null ? null : Names.GetValueOrDefault(type, type);
}

/// <summary>Derived license state as of a date (BR-FARM-06); never stored.</summary>
public enum LicenseState
{
    Valid,
    ExpiringSoon,
    Expired,
    NotYetEffective,
    Replaced,
}

/// <summary>The facts the state of one license depends on.</summary>
/// <param name="ReplacedOn">The business date the license that replaces it was recorded, or null.</param>
public sealed record LicenseFacts(DateOnly? EffectiveDate, DateOnly? ExpiryDate, DateOnly? ReplacedOn);

/// <summary>BR-FARM-06 license state rules, as a pure function so boundaries are unit tested.</summary>
public static class LicenseStateRules
{
    public const int ExpiringSoonDays = 30;

    /// <summary>
    /// As of <paramref name="day"/>: Replaced from the day a newer license replacing it was recorded, whatever the dates;
    /// otherwise Not yet effective before the effective date, Expired after the expiry date, Expiring soon when 30 days
    /// or fewer remain (the expiry day included), else Valid.
    /// </summary>
    public static LicenseState StateOn(LicenseFacts license, DateOnly day)
    {
        if (license.ReplacedOn is { } replacedOn && replacedOn <= day)
        {
            return LicenseState.Replaced;
        }

        if (license.EffectiveDate > day)
        {
            return LicenseState.NotYetEffective;
        }

        if (license.ExpiryDate is { } expiry)
        {
            if (expiry < day)
            {
                return LicenseState.Expired;
            }

            if (expiry.DayNumber - day.DayNumber <= ExpiringSoonDays)
            {
                return LicenseState.ExpiringSoon;
            }
        }

        return LicenseState.Valid;
    }
}
