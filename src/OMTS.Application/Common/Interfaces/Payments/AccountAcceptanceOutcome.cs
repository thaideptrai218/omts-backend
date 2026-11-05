namespace OMTS.Application.Common.Interfaces.Payments;

public enum AccountAcceptanceOutcome
{
    Accepted,

    /// <summary>The service answered and does not accept this account.</summary>
    Rejected,

    /// <summary>The service could not be asked, failed or did not answer in time.</summary>
    Unavailable,
}
