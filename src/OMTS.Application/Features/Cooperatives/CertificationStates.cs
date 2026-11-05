using OMTS.Application.Common;
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
    /// <summary>The longest a read URL of a certificate file lives on the provenance page.</summary>
    public static readonly TimeSpan PublicFileLinkLifetime = TimeSpan.FromMinutes(10);

    /// <summary>
    /// As of the business date <paramref name="day"/>: Expired once the expiry date has passed, Expiring soon when 30 days
    /// or fewer remain (the expiry day itself included), else Valid. A certification without an expiry date (a row saved
    /// before the date became required) is Valid. The same window as zone licenses
    /// (<see cref="LicenseStateRules.ExpiringSoonDays"/>).
    /// </summary>
    public static CertificationState StateOn(DateOnly? expiryDate, DateOnly day) => expiryDate switch
    {
        null => CertificationState.Valid,
        { } expiry when expiry < day => CertificationState.Expired,
        { } expiry when expiry.DayNumber - day.DayNumber <= LicenseStateRules.ExpiringSoonDays => CertificationState.ExpiringSoon,
        _ => CertificationState.Valid,
    };

    /// <summary>
    /// How long a read URL of the file of a certification that is not Expired may live on the provenance page at
    /// <paramref name="now"/>: <see cref="PublicFileLinkLifetime"/>, cut on the expiry day so the URL ends with the last
    /// second of that day. Signed URLs work up to and through their last whole second, so ending at the business midnight
    /// itself would leave one second of an Expired certification. Zero or less in that last second: a URL that dies at once.
    /// </summary>
    public static TimeSpan PublicFileLifetime(DateOnly? expiryDate, DateTimeOffset now)
    {
        var today = BusinessClock.ToBusinessDate(now);
        if (expiryDate != today)
        {
            return PublicFileLinkLifetime;
        }

        var lastSecond = BusinessClock.FromLocal(today.AddDays(1).ToDateTime(TimeOnly.MinValue)).AddSeconds(-1);
        var left = lastSecond - now;
        return left < PublicFileLinkLifetime ? left : PublicFileLinkLifetime;
    }
}
