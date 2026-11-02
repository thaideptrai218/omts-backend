using OMTS.Application.Features.Farming.Licenses.Models;

namespace OMTS.Application.Features.Farming.Licenses;

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
