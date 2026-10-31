using OMTS.Application.Features.Farming.Licenses;

namespace OMTS.Application.Features.Cooperatives;

/// <summary>Derived state of a cooperative certification (BR-FARM-06); never stored.</summary>
public enum CertificationState
{
    Valid,
    ExpiringSoon,
    Expired,
}

/// <summary>BR-FARM-06 for certifications, as a pure function so the 30-day boundary is unit tested.</summary>
public static class CertificationStateRules
{
    /// <summary>
    /// As of the business date <paramref name="day"/>: Expired once the expiry date has passed, Expiring soon when 30 days
    /// or fewer remain (the expiry day itself included), else Valid. A certification without an expiry date is Valid.
    /// The same window as zone licenses (<see cref="LicenseStateRules.ExpiringSoonDays"/>).
    /// </summary>
    public static CertificationState StateOn(DateOnly? expiryDate, DateOnly day) => expiryDate switch
    {
        null => CertificationState.Valid,
        { } expiry when expiry < day => CertificationState.Expired,
        { } expiry when expiry.DayNumber - day.DayNumber <= LicenseStateRules.ExpiringSoonDays => CertificationState.ExpiringSoon,
        _ => CertificationState.Valid,
    };
}
