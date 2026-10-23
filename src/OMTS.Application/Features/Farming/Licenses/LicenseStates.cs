namespace OMTS.Application.Features.Farming.Licenses;

/// <summary>Zone license types (FARM-03) with their display names.</summary>
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

    /// <summary>A zone without a Valid or Expiring soon document of these types shows a Missing row.</summary>
    public static readonly IReadOnlyList<string> Mandatory = [SeaAreaAllocation, AquacultureLicense];
}

/// <summary>Derived license state as of a date; never stored.</summary>
public enum LicenseState
{
    Valid,
    ExpiringSoon,
    Expired,
    NotYetEffective,
    Superseded,
    Missing,
}

/// <summary>The facts the state of one license depends on.</summary>
public sealed record LicenseFacts(DateOnly? EffectiveDate, DateOnly? ExpiryDate, IReadOnlyList<DateOnly?> ReplacementEffectiveDates);

/// <summary>FARM-03 state rules, as a pure function so boundaries are unit tested.</summary>
public static class LicenseStateRules
{
    public const int ExpiringSoonDays = 30;

    /// <summary>
    /// As of <paramref name="day"/>: Not yet effective if it starts later; Superseded if a replacement is
    /// already effective; Expired if the expiry date has passed; Expiring soon within 30 days; else Valid.
    /// </summary>
    public static LicenseState StateOn(LicenseFacts license, DateOnly day)
    {
        if (license.EffectiveDate > day)
        {
            return LicenseState.NotYetEffective;
        }

        if (license.ReplacementEffectiveDates.Any(d => d is not null && d <= day))
        {
            return LicenseState.Superseded;
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

    /// <summary>Worst state shown in the zone list: Expired, then Expiring soon, then Missing; null when all is fine.</summary>
    public static LicenseState? Worst(IEnumerable<LicenseState> states)
    {
        var all = states.ToList();
        return all.Contains(LicenseState.Expired) ? LicenseState.Expired
            : all.Contains(LicenseState.ExpiringSoon) ? LicenseState.ExpiringSoon
            : all.Contains(LicenseState.Missing) ? LicenseState.Missing
            : null;
    }
}
